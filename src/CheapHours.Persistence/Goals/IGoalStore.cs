using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Plans;

namespace CheapHours.Persistence.Goals;

/// <summary>Charging goals and the device claims that keep one open goal per vehicle and charger.</summary>
public interface IGoalStore
{
    /// <summary>
    /// Stores a new goal with its first plan version: claims both devices, inserts the goal and inserts version 1,
    /// in one transaction.
    /// </summary>
    /// <param name="goal">The goal.</param>
    /// <param name="firstVersion">Its first plan version.</param>
    /// <param name="now">The current time (UTC); claims that ended before it are free.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="GoalWriteOutcome.Stored"/>, or <see cref="GoalWriteOutcome.DeviceBusy"/> with nothing written.</returns>
    Task<GoalWriteOutcome> CreateAsync(StoredGoal goal, StoredPlan firstVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a changed target or departure with the new plan version: moves the claims' end to the new departure,
    /// updates the goal, inserts the version and supersedes the previous one, in one transaction.
    /// </summary>
    /// <param name="goal">The goal with its new values.</param>
    /// <param name="nextVersion">The new plan version.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="GoalWriteOutcome.Stored"/>, or <see cref="GoalWriteOutcome.VersionTaken"/> with nothing written.</returns>
    Task<GoalWriteOutcome> ChangeAsync(StoredGoal goal, StoredPlan nextVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a change draft above a running confirmed version: inserts the draft and supersedes earlier drafts
    /// above the running version, which stays confirmed. The goal and its claims are left alone.
    /// </summary>
    /// <param name="draft">The change draft; its number is one above the current version.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="GoalWriteOutcome.Stored"/>, or <see cref="GoalWriteOutcome.VersionTaken"/> with nothing written.</returns>
    Task<GoalWriteOutcome> AddChangeDraftAsync(StoredPlan draft, CancellationToken cancellationToken);

    /// <summary>
    /// Confirms a draft, in one transaction: the draft becomes <c>Confirmed</c> at <paramref name="now"/>, the running
    /// version (if any) becomes <c>Superseded</c> and its unsent commands <c>Cancelled</c>, a change draft's values are
    /// applied to the goal and its claims, and the new commands are stored with their outbox entries.
    /// </summary>
    /// <param name="draft">The draft to confirm, as read.</param>
    /// <param name="running">The running version it replaces, as read, or <see langword="null"/>.</param>
    /// <param name="changedGoal">For a change draft, the goal with the proposed values; otherwise <see langword="null"/>.</param>
    /// <param name="commands">The commands the confirmed version asks for.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="GoalWriteOutcome.Stored"/>, or <see cref="GoalWriteOutcome.PlanChanged"/> with nothing written.</returns>
    Task<GoalWriteOutcome> ConfirmAsync(StoredPlan draft, StoredPlan? running, StoredGoal? changedGoal, CommandBatch commands, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Re-plans a running confirmed version, in one transaction: stores <paramref name="nextVersion"/> (confirmed) above every
    /// version, supersedes the running version and any change draft above it, cancels the running version's unsent
    /// commands and stores the new commands with their outbox entries.
    /// </summary>
    /// <param name="nextVersion">The new confirmed version.</param>
    /// <param name="running">The running version it replaces, as read.</param>
    /// <param name="commands">The commands the new version asks for.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="GoalWriteOutcome.Stored"/>, or <see cref="GoalWriteOutcome.VersionTaken"/> or <see cref="GoalWriteOutcome.PlanChanged"/> with nothing written.</returns>
    Task<GoalWriteOutcome> ReplanConfirmedAsync(StoredPlan nextVersion, StoredPlan running, CommandBatch commands, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Reads one of <paramref name="ownerId"/>'s goals.</summary>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="goalId">The goal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The goal, or <see langword="null"/> when it does not exist or belongs to another owner.</returns>
    Task<StoredGoal?> GetAsync(OwnerId ownerId, GoalId goalId, CancellationToken cancellationToken);

    /// <summary>Lists every owner's goals that depart after <paramref name="departingAfter"/>; for the trusted worker only.</summary>
    /// <param name="departingAfter">Only goals departing after this time (UTC).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The goals, earliest departure first.</returns>
    Task<IReadOnlyList<StoredGoal>> ListOpenAsync(DateTimeOffset departingAfter, CancellationToken cancellationToken);

    /// <summary>Lists <paramref name="ownerId"/>'s goals departing in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="fromUtc">The earliest departure included (UTC).</param>
    /// <param name="toUtc">The first departure excluded (UTC).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The goals, earliest departure first.</returns>
    Task<IReadOnlyList<StoredGoal>> ListDepartingAsync(OwnerId ownerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}
