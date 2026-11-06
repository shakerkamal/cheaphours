using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Goals;

/// <summary>A stored goal with its plan id and timestamps.</summary>
/// <param name="Goal">The goal.</param>
/// <param name="PlanId">The id of the goal's one plan; its versions count up.</param>
/// <param name="CreatedAt">When the goal was created (UTC).</param>
/// <param name="UpdatedAt">When the goal was last changed (UTC).</param>
public sealed record StoredGoal(Goal Goal, PlanId PlanId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Returns whether the goal is open at <paramref name="nowUtc"/>: its departure has not passed.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns><see langword="true"/> while open.</returns>
    public bool IsOpenAt(DateTimeOffset nowUtc) => Goal.DepartureUtc > nowUtc;
}
