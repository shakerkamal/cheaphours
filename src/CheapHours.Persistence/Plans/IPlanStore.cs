using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Plans;

/// <summary>Plan versions. The current plan of a goal is its highest version.</summary>
public interface IPlanStore
{
    /// <summary>Reads the current (highest) version of <paramref name="goalId"/>'s plan.</summary>
    /// <param name="goalId">The goal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current version, or <see langword="null"/> when the goal has no plan.</returns>
    Task<StoredPlan?> GetCurrentAsync(GoalId goalId, CancellationToken cancellationToken);

    /// <summary>Reads the current (highest) version of one of <paramref name="ownerId"/>'s plans.</summary>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="planId">The plan.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current version, or <see langword="null"/> when the plan does not exist or belongs to another owner.</returns>
    Task<StoredPlan?> GetCurrentAsync(OwnerId ownerId, PlanId planId, CancellationToken cancellationToken);

    /// <summary>Reads the running version of <paramref name="goalId"/>'s plan: its highest <c>Confirmed</c> version.</summary>
    /// <param name="goalId">The goal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The running version, or <see langword="null"/> when no version is confirmed.</returns>
    Task<StoredPlan?> GetRunningAsync(GoalId goalId, CancellationToken cancellationToken);

    /// <summary>Reads every version of <paramref name="goalId"/>'s plan, oldest first.</summary>
    /// <param name="goalId">The goal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The versions.</returns>
    Task<IReadOnlyList<StoredPlan>> ListVersionsAsync(GoalId goalId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="version"/> and marks the version before it <c>Superseded</c>, in one transaction. When
    /// another writer stored the same version first, nothing is written.
    /// </summary>
    /// <param name="version">The new version; its number is one above the current one.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when stored; <see langword="false"/> when the version already existed.</returns>
    Task<bool> AddVersionAsync(StoredPlan version, CancellationToken cancellationToken);

    /// <summary>Reads, for each of <paramref name="goalIds"/>, its highest version that the owner confirmed.</summary>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="goalIds">The goals.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One version per goal that has a confirmed one.</returns>
    Task<IReadOnlyList<StoredPlan>> LatestConfirmedAsync(OwnerId ownerId, IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken);
}
