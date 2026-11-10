using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.IntegrationTests.Api;

/// <summary>Writes goals with a chosen plan version straight through the stores, for states the API cannot create.</summary>
internal static class PlanSeeds
{
    /// <summary>Stores a goal with fresh devices and one plan version, and returns the goal's id.</summary>
    public static async Task<GoalId> GoalAsync(
        IMongoDatabase database,
        OwnerId owner,
        DateTimeOffset departure,
        decimal expected,
        decimal arrival,
        PlanStatus status = PlanStatus.Draft,
        bool pricesComplete = true,
        DateTimeOffset? madeAt = null,
        IReadOnlyList<DateTimeOffset>? slotStarts = null,
        GoalId? goalId = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var made = madeAt ?? DateTimeOffset.UtcNow;
        var goal = new StoredGoal(
            new Goal(goalId ?? GoalId.Parse("goal_" + suffix), owner, DeviceId.Parse("dev_car" + suffix), DeviceId.Parse("dev_box" + suffix), 80, departure),
            PlanId.Parse("plan_" + suffix),
            made,
            made);
        var plan = new Plan(
            goal.PlanId,
            goal.Goal.Id,
            owner,
            1,
            status,
            slotStarts is null ? [new PlanSlot(Core.SlotGrid.Floor(departure).AddHours(-3), 11_000)] : [.. slotStarts.Select(s => new PlanSlot(s, 11_000))],
            expected,
            arrival,
            targetMissed: false,
            pricesComplete,
            made,
            status == PlanStatus.Draft ? made.AddMinutes(10) : null);
        var stored = new StoredPlan(plan, PlanReason.Created, 40m, 80, 26_667, status == PlanStatus.Confirmed ? made : null);

        // Claims lapse at departure, so "now" for the claim check is the time the goal was made.
        Assert.Equal(GoalWriteOutcome.Stored, await new GoalStore(database).CreateAsync(goal, stored, made, TestContext.Current.CancellationToken));
        return goal.Goal.Id;
    }
}
