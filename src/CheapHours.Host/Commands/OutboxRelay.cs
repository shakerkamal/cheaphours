using CheapHours.Core.Commands;
using CheapHours.Host.Goals;
using CheapHours.Host.Worker;
using CheapHours.Messaging;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.Outbox;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Commands;

/// <summary>
/// Publishes the outbox (command-dispatch design §3). Every <see cref="CommandsOptions.RelayInterval"/>, under the
/// <c>outbox-relay</c> lease, it reads due entries oldest first, publishes each with its saved envelope, and marks
/// it published only after the broker confirms. An entry that announces a command then moves the command from
/// <c>Pending</c> to <c>Published</c>. While the broker is down it stops the batch and backs off from 1 s to 30 s;
/// the entries wait in MongoDB. A crash between the confirm and the mark publishes the entry again, which the
/// dispatcher's claim absorbs.
/// </summary>
internal sealed partial class OutboxRelay(
    [FromKeyedServices(LeaseNames.OutboxRelay)] ILease lease,
    IOutboxStore outbox,
    ICommandStore commands,
    IMessagePublisher publisher,
    WorkerState state,
    IOptions<CommandsOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    /// <summary>How long one run holds the relay lease; a run gets half of it.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly CommandsOptions _options = options.Value;
    private readonly string _holder = ReplicaIds.From(configuration);

    /// <summary>Runs one batch if this replica can take the lease.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>How the run went.</returns>
    public async Task<RelayRun> RunOnceAsync(CancellationToken cancellationToken)
    {
        var run = new RelayRun();
        await LeasedRun.RunAsync(lease, _holder, LeaseDuration, time, ct => PublishDueAsync(run, ct), cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            TimeSpan? backoff = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(backoff ?? _options.RelayInterval, time, stoppingToken).ConfigureAwait(false);
                try
                {
                    var run = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    backoff = run.BrokerUnavailable ? Next(backoff) : null;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // MongoDB unreachable or a bug in one entry: the entries stay, and the next run tries again.
                    LogRunFailed(ex.GetType().Name);
                    backoff = Next(backoff);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan Next(TimeSpan? backoff) =>
        backoff is { } current ? TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaxBackoff.Ticks)) : MinBackoff;

    private async Task PublishDueAsync(RelayRun run, CancellationToken cancellationToken)
    {
        var due = await outbox.ListDueAsync(time.GetUtcNow(), _options.RelayBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var entry in due)
        {
            try
            {
                await publisher.PublishSerializedAsync(CommandOutbox.ToMessage(entry), entry.Destination, entry.Route == OutboxRoute.Queue, cancellationToken).ConfigureAwait(false);
            }
            catch (BrokerUnavailableException ex)
            {
                await outbox.RecordFailedAttemptAsync(entry.MessageId, cancellationToken).ConfigureAwait(false);
                LogBrokerUnavailable(ex.InnerException?.GetType().Name ?? ex.GetType().Name);
                run.BrokerUnavailable = true;
                return;
            }

            var now = time.GetUtcNow();
            await outbox.MarkPublishedAsync(entry.MessageId, now, cancellationToken).ConfigureAwait(false);
            run.Published++;
            if (entry.CommandId is { } commandId)
            {
                await MarkCommandPublishedAsync(commandId, now, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Pending → Published; a command another writer already moved on is left as it is.
    private async Task MarkCommandPublishedAsync(Core.Ids.CommandId commandId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await commands.FindAsync(commandId, cancellationToken).ConfigureAwait(false) is not { } stored
                || !stored.Command.CanFire(CommandTrigger.Publish))
            {
                return;
            }

            var last = stored.Command.History[^1].At;
            stored.Command.Publish(now > last ? now : last);
            if (await commands.SaveAsync(stored, cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The broker did not take an outbox entry ({Reason}); backing off, the entries wait")]
    private partial void LogBrokerUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay run failed ({ExceptionType}); the next run tries again")]
    private partial void LogRunFailed(string exceptionType);

    /// <summary>What one run did.</summary>
    public sealed class RelayRun
    {
        /// <summary>Gets or sets how many entries the broker confirmed.</summary>
        public int Published { get; set; }

        /// <summary>Gets or sets whether the broker refused an entry, which ended the batch.</summary>
        public bool BrokerUnavailable { get; set; }
    }
}
