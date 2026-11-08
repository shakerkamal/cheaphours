using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.Host.Goals;

/// <summary>What confirming a plan gave; the API maps each case to one status code.</summary>
internal abstract record ConfirmOutcome
{
    private ConfirmOutcome()
    {
    }

    /// <summary>The draft is confirmed and its commands are stored (202).</summary>
    /// <param name="Goal">The goal, with a change draft's values applied.</param>
    /// <param name="Plan">The confirmed version.</param>
    /// <param name="Commands">The commands it created, earliest due first.</param>
    public sealed record Confirmed(StoredGoal Goal, StoredPlan Plan, IReadOnlyList<StoredCommand> Commands) : ConfirmOutcome;

    /// <summary>The plan or its goal does not exist for this owner (404).</summary>
    public sealed record NotFound : ConfirmOutcome;

    /// <summary>The current version is a draft that has expired (410).</summary>
    public sealed record Expired : ConfirmOutcome;

    /// <summary>The current version is not a draft, or the plan moved on while confirming (409).</summary>
    /// <param name="Detail">What conflicts, for the problem's detail.</param>
    public sealed record Conflict(string Detail) : ConfirmOutcome;
}
