using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Outbox;

namespace CheapHours.Persistence.Commands;

/// <summary>
/// The <c>commands</c> collection. State changes go through Core: callers load a command, fire a trigger and save
/// it against the version they read. A save that finds a newer version writes nothing and returns
/// <see langword="null"/>; the caller reloads and decides again.
/// </summary>
public interface ICommandStore
{
    /// <summary>Stores a new command with the outbox entry that announces it, in one transaction.</summary>
    /// <param name="command">The command, at version 0.</param>
    /// <param name="announcement">Its outbox entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when stored; <see langword="false"/> when a command with the same id or idempotency key exists, with nothing written.</returns>
    Task<bool> CreateWithOutboxAsync(StoredCommand command, OutboxEntry announcement, CancellationToken cancellationToken);

    /// <summary>Reads one of <paramref name="ownerId"/>'s commands.</summary>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="commandId">The command.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The command, or <see langword="null"/> when it does not exist or belongs to another owner.</returns>
    Task<StoredCommand?> GetAsync(OwnerId ownerId, CommandId commandId, CancellationToken cancellationToken);

    /// <summary>Reads a command by id; for the trusted worker only.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The command, or <see langword="null"/>.</returns>
    Task<StoredCommand?> FindAsync(CommandId commandId, CancellationToken cancellationToken);

    /// <summary>Saves a command read at <see cref="StoredCommand.Version"/>.</summary>
    /// <param name="command">The changed command.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The saved command at its new version, or <see langword="null"/> when another writer saved first.</returns>
    Task<StoredCommand?> SaveAsync(StoredCommand command, CancellationToken cancellationToken);

    /// <summary>Saves a command and adds an outbox entry for it in one transaction (a resend or a re-announcement).</summary>
    /// <param name="command">The changed command.</param>
    /// <param name="announcement">The outbox entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The saved command at its new version, or <see langword="null"/> when another writer saved first, with nothing written.</returns>
    Task<StoredCommand?> SaveWithOutboxAsync(StoredCommand command, OutboxEntry announcement, CancellationToken cancellationToken);

    /// <summary>
    /// Claims a command for sending: it must be <c>Published</c>, due, past its next attempt time, and free of a live
    /// claim. Two dispatchers racing for one command get exactly one claim.
    /// </summary>
    /// <param name="commandId">The command.</param>
    /// <param name="claimant">Who claims it.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="duration">How long the claim lasts.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The claimed command, or <see langword="null"/> when it could not be claimed.</returns>
    Task<StoredCommand?> TryClaimAsync(CommandId commandId, string claimant, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Drops <paramref name="claimant"/>'s claim on a command, if it still holds one.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="claimant">Who claimed it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a claim was dropped.</returns>
    Task<bool> ReleaseClaimAsync(CommandId commandId, string claimant, CancellationToken cancellationToken);

    /// <summary>Clears every claim that lapsed at or before <paramref name="now"/>.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many claims were cleared.</returns>
    Task<long> ExpireClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Lists commands in <paramref name="state"/>, earliest due first.</summary>
    /// <param name="state">The state.</param>
    /// <param name="limit">The most commands to return; greater than zero.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="limit"/> commands.</returns>
    Task<IReadOnlyList<StoredCommand>> ListInStateAsync(CommandState state, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists <c>Published</c> commands that could be sent at <paramref name="now"/>: due, past their next attempt time
    /// and free of a live claim; earliest due first.
    /// </summary>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="limit">The most commands to return; greater than zero.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="limit"/> commands.</returns>
    Task<IReadOnlyList<StoredCommand>> ListSendableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Lists a device's commands that are not in a terminal state, earliest due first.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commands.</returns>
    Task<IReadOnlyList<StoredCommand>> ListOpenForDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Lists the commands every version of a plan made, earliest due first.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commands.</returns>
    Task<IReadOnlyList<StoredCommand>> ListForPlanAsync(PlanId planId, CancellationToken cancellationToken);

    /// <summary>Lists the commands a plan version made, earliest due first.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commands.</returns>
    Task<IReadOnlyList<StoredCommand>> ListForPlanVersionAsync(PlanId planId, int version, CancellationToken cancellationToken);
}
