using CheapHours.Core.Commands;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Commands;

namespace CheapHours.Host.Commands;

/// <summary>
/// Applies the gateway replicas' answers on <c>worker.command-results</c> (command-dispatch design §8), as the
/// dispatcher does for a route that answers at once. A result for a command that is no longer <c>Published</c> (a
/// duplicate, or one for a command that moved on) fails <c>CanFire</c> and is dropped. A result after the claim
/// lapsed still applies while the command is <c>Published</c>.
/// </summary>
internal sealed partial class CommandResultConsumer(
    ICommandStore commands,
    CommandOutcomes outcomes,
    ILogger<CommandResultConsumer> logger) : IBatchHandler<GatewayCommandResult>
{
    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<Delivered<GatewayCommandResult>> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
            foreach (var delivered in batch)
            {
                await ApplyAsync(delivered.Message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (CommandStoreOutage.Is(ex))
        {
            throw new DependencyUnavailableException("MongoDB is not reachable.", ex);
        }
    }

    private async Task ApplyAsync(GatewayCommandResult result, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await commands.FindAsync(result.CommandId, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                LogDropped(result.CommandId.Value, "unknown command");
                return;
            }

            if (stored.Command.State != CommandState.Published)
            {
                LogNotPublished(result.CommandId.Value, stored.Command.State);
                return;
            }

            // Stamped with the time the gateway received the charger's answer, on the gateway's clock like the status
            // receipt times: a charger answers before it reports its new state, so that report counts as after the
            // send even when it reaches the gateway before this result reaches the worker.
            var applied = outcomes.Apply(stored, result.Outcome, result.OccurredAt);
            if (await commands.SaveAsync(applied, cancellationToken).ConfigureAwait(false) is { } saved)
            {
                LogApplied(saved.Id.Value, saved.Command.State);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway result for command {CommandId}: {State}")]
    private partial void LogApplied(string commandId, CommandState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway result for command {CommandId} dropped: the command is {State}")]
    private partial void LogNotPublished(string commandId, CommandState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway result for command {CommandId} dropped: {Reason}")]
    private partial void LogDropped(string commandId, string reason);
}
