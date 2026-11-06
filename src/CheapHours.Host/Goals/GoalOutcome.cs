using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.Host.Goals;

/// <summary>What creating or changing a goal gave; the API maps each case to one status code.</summary>
internal abstract record GoalOutcome
{
    private GoalOutcome()
    {
    }

    /// <summary>The goal is stored with its current plan version (201 or 200).</summary>
    /// <param name="Goal">The goal.</param>
    /// <param name="Plan">Its current plan version.</param>
    public sealed record Planned(StoredGoal Goal, StoredPlan Plan) : GoalOutcome;

    /// <summary>The goal, the vehicle or the charger does not exist for this owner (404).</summary>
    public sealed record NotFound : GoalOutcome;

    /// <summary>The goal's state forbids the request (409).</summary>
    /// <param name="Detail">What conflicts, for the problem's detail.</param>
    public sealed record Conflict(string Detail) : GoalOutcome;

    /// <summary>The request is well formed but cannot be planned (422).</summary>
    /// <param name="Detail">Why, for the problem's detail.</param>
    public sealed record Unprocessable(string Detail) : GoalOutcome;
}
