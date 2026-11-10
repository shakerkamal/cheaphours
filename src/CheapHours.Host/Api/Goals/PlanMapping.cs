using CheapHours.Host.Api.Dtos;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.Host.Api.Goals;

/// <summary>
/// Maps stored goals and plan versions to their <c>/v1</c> shapes. Costs are shown to the cent, half away from
/// zero, and savings are the rounded arrival cost minus the rounded expected cost, so the three figures add up.
/// A draft past its expiry is shown <c>Expired</c> without a write.
/// </summary>
internal static class PlanMapping
{
    /// <summary>Rounds an amount to the cent, half away from zero.</summary>
    /// <param name="eur">The amount in EUR.</param>
    /// <returns>The rounded amount.</returns>
    public static decimal Cents(decimal eur) => Math.Round(eur, 2, MidpointRounding.AwayFromZero);

    public static GoalDto ToDto(StoredGoal goal) => new(
        goal.Goal.Id, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, goal.Goal.TargetSocPercent, goal.Goal.DepartureUtc);

    public static GoalSummaryDto ToSummary(StoredGoal goal, StoredPlan current, DateTimeOffset now)
    {
        var plan = ToDto(current, now);
        return new GoalSummaryDto(
            goal.Goal.Id,
            goal.Goal.VehicleDeviceId,
            goal.Goal.ChargerDeviceId,
            goal.Goal.TargetSocPercent,
            goal.Goal.DepartureUtc,
            new PlanSummaryDto(plan.Id, plan.Version, plan.Status, plan.ExpiresAt));
    }

    public static PlanDto ToDto(StoredPlan stored, DateTimeOffset now)
    {
        var plan = stored.Plan;
        var expected = Cents(plan.ExpectedCostEur);
        var arrival = Cents(plan.ArrivalCostEur);
        return new PlanDto(
            plan.Id,
            plan.GoalId,
            plan.Version,
            plan.StatusAsOf(now),
            [.. plan.Slots.Select(s => new PlanSlotDto(s.StartUtc, s.EndUtc, s.PowerW))],
            expected,
            arrival,
            arrival - expected,
            plan.TargetMissed,
            plan.PricesComplete,
            plan.ExpiresAtUtc,
            stored.IsChangeDraft ? stored.ProposedTargetSocPercent ?? stored.TargetSocPercent : null,
            stored.ProposedDepartureAt);
    }
}
