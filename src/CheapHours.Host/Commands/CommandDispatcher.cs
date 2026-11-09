using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Observability;
using CheapHours.Host.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Commands;

/// <summary>
/// Sends commands on <c>worker.dispatch</c> (command-dispatch design §5). MongoDB is the source of truth and the
/// message only a wake-up call, so a message is acknowledged without a send whenever the command is not ready:
/// terminal, already sent, not due, waiting for a retry, or its device busy with another command or an earlier-due
/// one. Otherwise the dispatcher claims the command, so a repeated message sends it once, routes it and applies the
/// outcome through Core. The command monitor announces a command again when it becomes ready.
/// </summary>
internal sealed partial class CommandDispatcher(
    ICommandStore commands,
    IDeviceCommandStateStore devices,
    IEnumerable<ICommandRoute> routes,
    CommandOutcomes outcomes,
    CommandMetrics metrics,
    MessagingTopology topology,
    IOptions<CommandsOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CommandDispatcher> logger) : IBatchHandler<CommandRequested>
{
    private readonly CommandsOptions _options = options.Value;
    private readonly ICommandRoute[] _routes = [.. routes];
    private readonly string _holder = ReplicaIds.From(configuration);

    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<Delivered<CommandRequested>> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
            foreach (var delivered in batch)
            {
                // Each message is handled under its own trace context: the request that created the command.
                using var process = MessagingTracing.StartProcess(topology.Dispatch.Main, delivered.Message);
                try
                {
                    await DispatchAsync(delivered.Message.CommandId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    process.Fail(ex);
                    throw;
                }
            }
        }
        catch (Exception ex) when (CommandStoreOutage.Is(ex))
        {
            throw new DependencyUnavailableException("MongoDB is not reachable.", ex);
        }
    }

    /// <summary>Tries to send one command; returns without a send when it is not ready.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>A task that completes when the command was sent, skipped or failed.</returns>
    public async Task DispatchAsync(CommandId commandId, CancellationToken cancellationToken)
    {
        if (await ReadyAsync(commandId, cancellationToken).ConfigureAwait(false) is not { } ready)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (await commands.TryClaimAsync(ready.Id, _holder, now, _options.ClaimDuration, cancellationToken).ConfigureAwait(false) is not { } claimed)
        {
            // Another dispatcher holds it, or it moved on since it was read.
            return;
        }

        var device = await devices.GetAsync(claimed.Command.Request.DeviceId, cancellationToken).ConfigureAwait(false);
        var providerId = device?.Device.ProviderId;
        using var span = CommandTracing.Start($"dispatch {claimed.Command.Request.Type}", claimed, providerId);
        using var scope = LogScopes.ForCommand(logger, claimed.Id, claimed.Command.Request.DeviceId, providerId);

        RouteOutcome outcome;
        try
        {
            outcome = await RouteAsync(claimed, device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken route: free the command so every redelivery fails the same way and the message ends in the
            // dead-letter queue; the command monitor dead-letters the command after its re-announcements.
            span.Fail(ex);
            await commands.ReleaseClaimAsync(claimed.Id, _holder, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (outcome is RouteOutcome.Completed completed)
        {
            Record(span, completed.Result);
            if (await ApplyAsync(claimed, completed.Result, cancellationToken).ConfigureAwait(false) is { Command.State: CommandState.DeadLettered } dead)
            {
                span.Fail("dead-lettered");
                metrics.DeadLettered(dead, providerId, CommandMetrics.AttemptsExhausted);
            }
        }
        else
        {
            span.Tag(CommandTracing.OutcomeTag, "forwarded");
            LogForwarded(claimed.Id.Value);
        }
    }

    // The send's classified outcome on the dispatch span; a refusal marks it failed with its reason code.
    private static void Record(SpanScope span, SendResult result)
    {
        switch (result)
        {
            case SendResult.Accepted:
                span.Tag(CommandTracing.OutcomeTag, "accepted");
                break;
            case SendResult.Rejected rejected:
                span.Tag(CommandTracing.OutcomeTag, "rejected").Tag(CommandTracing.ReasonTag, rejected.Reason).Fail("rejected");
                break;
            case SendResult.Transient:
                span.Tag(CommandTracing.OutcomeTag, "transient");
                break;
        }
    }

    // Loads the command and returns it when it may be sent now: Published (publishing a Pending one first), due,
    // past its retry time, and its device free of other work and of earlier-due commands.
    private async Task<StoredCommand?> ReadyAsync(CommandId commandId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = time.GetUtcNow();
            if (await commands.FindAsync(commandId, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                LogUnknown(commandId.Value);
                return null;
            }

            var command = stored.Command;
            if (command.State is not (CommandState.Pending or CommandState.Published) || command.DueAt > now)
            {
                return null;
            }

            if (command.State == CommandState.Pending)
            {
                // The relay published the message but has not marked the command yet.
                command.Publish(CommandOutcomes.NotBefore(command, now));
                if (await commands.SaveAsync(stored, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }
            }

            if (command.NextAttemptAt > now)
            {
                return null;
            }

            var open = await commands.ListOpenForDeviceAsync(command.Request.DeviceId, cancellationToken).ConfigureAwait(false);
            if (open.Any(other => other.Id != stored.Id && (IsBusy(other, now) || IsEarlier(other, stored))))
            {
                LogWaiting(stored.Id.Value);
                return null;
            }

            return stored;
        }

        return null;
    }

    private static bool IsBusy(StoredCommand other, DateTimeOffset now) =>
        other.Command.State is CommandState.Sent or CommandState.Reconciling || other.IsClaimedAt(now);

    private static bool IsEarlier(StoredCommand other, StoredCommand command) =>
        other.Command.State is CommandState.Pending or CommandState.Published
        && (other.Command.DueAt < command.Command.DueAt
            || (other.Command.DueAt == command.Command.DueAt && string.CompareOrdinal(other.Id.Value, command.Id.Value) < 0));

    private async Task<RouteOutcome> RouteAsync(StoredCommand command, StoredDevice? device, CancellationToken cancellationToken)
    {
        if (device is null)
        {
            return new RouteOutcome.Completed(new SendResult.Rejected("unknown-device"));
        }

        return _routes.FirstOrDefault(r => r.Reaches(device)) is { } route
            ? await route.SendAsync(command, device, cancellationToken).ConfigureAwait(false)
            : new RouteOutcome.Completed(new SendResult.Rejected("no-route"));
    }

    // Saves the outcome against the claimed version; if something else wrote meanwhile, applies it to a fresh copy
    // while the command is still waiting for this send's outcome.
    // Returns the command as saved, or null when nothing was saved.
    private async Task<StoredCommand?> ApplyAsync(StoredCommand claimed, SendResult result, CancellationToken cancellationToken)
    {
        var current = claimed;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var applied = outcomes.Apply(current, result, time.GetUtcNow());
            if (await commands.SaveAsync(applied, cancellationToken).ConfigureAwait(false) is { } saved)
            {
                LogApplied(saved.Id.Value, saved.Command.State, saved.Command.Attempts);
                return saved;
            }

            if (await commands.FindAsync(claimed.Id, cancellationToken).ConfigureAwait(false) is not { Command.State: CommandState.Published } fresh)
            {
                return null;
            }

            current = fresh;
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId}: {State} after {Attempts} attempt(s)")]
    private partial void LogApplied(string commandId, CommandState state, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} forwarded to its gateway replica")]
    private partial void LogForwarded(string commandId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Command {CommandId} waits: its device is busy or has an earlier command")]
    private partial void LogWaiting(string commandId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CommandRequested for unknown command {CommandId} dropped")]
    private partial void LogUnknown(string commandId);
}
