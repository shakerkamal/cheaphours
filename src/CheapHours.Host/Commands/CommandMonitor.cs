using CheapHours.Core.Commands;
using CheapHours.Host.Goals;
using CheapHours.Host.Worker;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Locks;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Commands;

/// <summary>
/// Owns every command transition that waits on the clock (command-dispatch design §9). Every
/// <see cref="CommandsOptions.MonitorInterval"/>, under the <c>command-monitor</c> lease, it runs four passes:
/// <list type="number">
/// <item><b>Confirm</b> a sent command when the device reports the requested state after the send (or, for a command
/// that was already in effect, a matching older report).</item>
/// <item><b>Time out</b> a sent command past the confirmation timeout (twice the report interval for a polled
/// device, which can only confirm at its next poll) and reconcile it: confirm, resend with the same key (a new
/// outbox entry in the same write), or dead-letter.</item>
/// <item><b>Announce</b> a published command that is due, free of a claim and of a busy device, and past its retry
/// time, when nothing has handed it over: 5 s after it became sendable, then every dispatch timeout. Only those
/// later announcements count; after <see cref="CommandsOptions.MaxReannouncements"/> the command is dead-lettered.</item>
/// <item><b>Expire</b> lapsed claims.</item>
/// </list>
/// </summary>
internal sealed partial class CommandMonitor(
    [FromKeyedServices(LeaseNames.CommandMonitor)] ILease lease,
    ICommandStore commands,
    IDeviceCommandStateStore devices,
    IEnumerable<IPollingProvider> pollingProviders,
    WorkerState state,
    IOptions<CommandsOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CommandMonitor> logger) : BackgroundService
{
    /// <summary>How long one run holds the monitor lease; a run gets half of it.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>How long a sendable command may wait for its message before the monitor announces it again.</summary>
    public static readonly TimeSpan ReannounceAfter = TimeSpan.FromSeconds(5);

    /// <summary>The reason a command is dead-lettered with when its announcements keep failing.</summary>
    public const string DispatchKeptFailing = "dispatch kept failing";

    private const int ScanLimit = 500;

    private readonly CommandsOptions _options = options.Value;
    private readonly RetryPolicyOptions _retry = options.Value.ToRetryPolicyOptions();
    private readonly HashSet<string> _polled = pollingProviders.Select(p => p.ProviderId).ToHashSet(StringComparer.Ordinal);
    private readonly string _holder = ReplicaIds.From(configuration);

    /// <summary>Runs the four passes once if this replica can take the lease.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns><see langword="true"/> when this replica ran them.</returns>
    public Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        LeasedRun.RunAsync(lease, _holder, LeaseDuration, time, MonitorAsync, cancellationToken);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.MonitorInterval, time, stoppingToken).ConfigureAwait(false);
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogRunFailed(ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        await ConfirmOrTimeOutAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
        await commands.ExpireClaimsAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    // Passes 1 and 2. A save that loses to another writer is dropped; the next run decides again.
    private async Task ConfirmOrTimeOutAsync(CancellationToken cancellationToken)
    {
        foreach (var stored in await commands.ListInStateAsync(CommandState.Sent, ScanLimit, cancellationToken).ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            var device = await devices.GetAsync(stored.Command.Request.DeviceId, cancellationToken).ConfigureAwait(false);
            var observation = Observe(device, now);
            var command = stored.Command;
            var at = CommandOutcomes.NotBefore(command, now);

            if (CommandConfirmation.IsConfirmed(command, observation, stored.AlreadyInEffect))
            {
                command.Confirm(at);
                await SaveAsync(stored, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (now - command.LastSentAt < ConfirmationTimeout(device))
            {
                continue;
            }

            command.TimeOut(at);
            switch (ReconcileDecider.Decide(command, observation, _retry))
            {
                case ReconcileDecision.Confirm:
                    command.Confirm(at);
                    await SaveAsync(stored, cancellationToken).ConfigureAwait(false);
                    break;
                case ReconcileDecision.Resend:
                    command.Resend(at);
                    var resent = stored with { Announcements = 0, LastAnnouncedAt = now, AlreadyInEffect = false };
                    await commands.SaveWithOutboxAsync(resent, CommandOutbox.Announce(resent, now, availableAt: now), cancellationToken).ConfigureAwait(false);
                    LogResent(stored.Id.Value, command.Attempts);
                    break;
                case ReconcileDecision.Fail fail:
                    command.GiveUp(at, fail.Reason);
                    await SaveAsync(stored, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    // Pass 3: per device, only the earliest-due sendable command, and only when the device is free.
    private async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        var sendable = await commands.ListSendableAsync(time.GetUtcNow(), ScanLimit, cancellationToken).ConfigureAwait(false);
        foreach (var first in sendable.GroupBy(c => c.Command.Request.DeviceId).Select(g => g.First()))
        {
            var now = time.GetUtcNow();
            var open = await commands.ListOpenForDeviceAsync(first.Command.Request.DeviceId, cancellationToken).ConfigureAwait(false);
            if (open.Any(c => c.Id != first.Id && (c.Command.State is CommandState.Sent or CommandState.Reconciling || c.IsClaimedAt(now))))
            {
                continue;
            }

            var command = first.Command;
            var sendableSince = Latest(command.History[^1].At, command.DueAt, command.NextAttemptAt);
            var announcedSince = first.LastAnnouncedAt is { } last && last >= sendableSince;
            if (!announcedSince)
            {
                // Nothing announced it since it became sendable: give the relay or the dispatcher a moment first.
                if (now - sendableSince >= ReannounceAfter)
                {
                    await AnnounceAsync(first with { LastAnnouncedAt = now }, now, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (now - first.LastAnnouncedAt < _options.DispatchTimeout)
            {
                continue;
            }

            if (first.Announcements >= _options.MaxReannouncements)
            {
                command.GiveUp(CommandOutcomes.NotBefore(command, now), DispatchKeptFailing);
                await SaveAsync(first, cancellationToken).ConfigureAwait(false);
                LogDeadLettered(first.Id.Value, first.Announcements);
                continue;
            }

            await AnnounceAsync(first with { LastAnnouncedAt = now, Announcements = first.Announcements + 1 }, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnounceAsync(StoredCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await commands.SaveWithOutboxAsync(command, CommandOutbox.Announce(command, now, availableAt: now), cancellationToken).ConfigureAwait(false) is not null)
        {
            LogAnnounced(command.Id.Value, command.Announcements);
        }
    }

    private async Task SaveAsync(StoredCommand command, CancellationToken cancellationToken)
    {
        if (await commands.SaveAsync(command, cancellationToken).ConfigureAwait(false) is { } saved)
        {
            LogMoved(saved.Id.Value, saved.Command.State);
        }
    }

    // A polled device can only show a command's effect at its next poll, so it gets two report intervals.
    private TimeSpan ConfirmationTimeout(StoredDevice? device)
    {
        var timeout = _options.ConfirmationTimeout;
        if (device is not null && _polled.Contains(device.Device.ProviderId) && device.Device.State?.ReportInterval is { } interval && interval * 2 > timeout)
        {
            timeout = interval * 2;
        }

        return timeout;
    }

    // The device's status as of now, observed at the later of the status's receipt and the last reading.
    private static DeviceObservation? Observe(StoredDevice? device, DateTimeOffset now)
    {
        if (device?.Device.State is not { } deviceState)
        {
            return null;
        }

        var reading = deviceState.LastReading;
        var observedAt = Latest(device.StatusReceivedAt, reading?.MeasurementTime);
        return observedAt is { } at ? new DeviceObservation(device.Device.StatusAsOf(now), reading?.PowerW, at) : null;
    }

    private static DateTimeOffset Latest(DateTimeOffset first, params DateTimeOffset?[] others) =>
        others.Aggregate(first, (latest, other) => other > latest ? other.Value : latest);

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId}: {State}")]
    private partial void LogMoved(string commandId, CommandState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} not confirmed in time; resent with the same key (attempts so far {Attempts})")]
    private partial void LogResent(string commandId, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} announced again (counted re-announcements {Count})")]
    private partial void LogAnnounced(string commandId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {CommandId} dead-lettered after {Count} re-announcements: dispatch kept failing")]
    private partial void LogDeadLettered(string commandId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command monitor run failed ({ExceptionType}); the next run tries again")]
    private partial void LogRunFailed(string exceptionType);
}
