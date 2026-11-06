using CheapHours.Persistence.Plans;

namespace CheapHours.Host.Goals;

/// <summary>What re-planning a goal did.</summary>
internal abstract record ReplanOutcome
{
    private ReplanOutcome()
    {
    }

    /// <summary>A new version was stored.</summary>
    /// <param name="Version">The new version.</param>
    public sealed record Added(StoredPlan Version) : ReplanOutcome;

    /// <summary>The plan came out the same (or another re-plan stored this version first); nothing was stored.</summary>
    public sealed record Unchanged : ReplanOutcome;

    /// <summary>The goal could not or should not be re-planned now; nothing was stored.</summary>
    /// <param name="Reason">Why, for the log.</param>
    public sealed record Skipped(string Reason) : ReplanOutcome;
}
