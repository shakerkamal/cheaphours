using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>Goals and plan versions for the store tests.</summary>
internal static class GoalPlanSamples
{
    public static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

    public static StoredGoal Goal(string id, string vehicle, string charger, DateTimeOffset departure, OwnerId? owner = null) => new(
        new Goal(GoalId.Parse(id), owner ?? Owner, DeviceId.Parse(vehicle), DeviceId.Parse(charger), 80, departure),
        PlanId.Parse("plan_" + id["goal_".Length..]),
        Now,
        Now);

    public static StoredPlan Version(
        StoredGoal goal,
        int version,
        PlanStatus status = PlanStatus.Draft,
        DateTimeOffset? madeAt = null,
        DateTimeOffset? confirmedAt = null,
        PlanReason reason = PlanReason.Created,
        decimal expected = 1.20m,
        decimal arrival = 2.00m,
        bool pricesComplete = true)
    {
        var made = madeAt ?? Now;
        var plan = new Plan(
            goal.PlanId,
            goal.Goal.Id,
            goal.Goal.OwnerId,
            version,
            status,
            [new PlanSlot(new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero), 11_000), new PlanSlot(new DateTimeOffset(2026, 11, 4, 2, 15, 0, TimeSpan.Zero), 11_000)],
            expected,
            arrival,
            targetMissed: false,
            pricesComplete,
            made,
            status == PlanStatus.Draft ? made.AddMinutes(10) : null);
        return new StoredPlan(plan, reason, 40.5m, goal.Goal.TargetSocPercent, 26_667, confirmedAt);
    }
}
