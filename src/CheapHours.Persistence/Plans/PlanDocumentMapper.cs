using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Plans;

/// <summary>Maps between <see cref="StoredPlan"/> and <see cref="PlanDocument"/>.</summary>
internal static class PlanDocumentMapper
{
    public static PlanDocument ToDocument(StoredPlan stored)
    {
        var plan = stored.Plan;
        return new PlanDocument
        {
            Id = stored.VersionId,
            PlanId = plan.Id.Value,
            Version = plan.Version,
            GoalId = plan.GoalId.Value,
            OwnerId = plan.OwnerId.Value,
            Status = plan.Status,
            Reason = stored.Reason,
            Slots = [.. plan.Slots.Select(s => new PlanDocument.SlotDocument { Start = UtcTime.ToBson(s.StartUtc), PowerW = s.PowerW })],
            ExpectedCostEur = plan.ExpectedCostEur,
            ArrivalCostEur = plan.ArrivalCostEur,
            TargetMissed = plan.TargetMissed,
            PricesComplete = plan.PricesComplete,
            StartSocPercent = stored.StartSocPercent,
            TargetSocPercent = stored.TargetSocPercent,
            EnergyNeededWh = stored.EnergyNeededWh,
            MadeAt = UtcTime.ToBson(plan.MadeAtUtc),
            ExpiresAt = plan.ExpiresAtUtc is { } expires ? UtcTime.ToBson(expires) : null,
            ConfirmedAt = stored.ConfirmedAt is { } confirmed ? UtcTime.ToBson(confirmed) : null,
            ProposedTargetSocPercent = stored.ProposedTargetSocPercent,
            ProposedDepartureAt = stored.ProposedDepartureAt is { } departure ? UtcTime.ToBson(departure) : null,
        };
    }

    public static StoredPlan ToStored(PlanDocument d)
    {
        // A superseded draft keeps its expiry in the document; the domain type drops it for non-drafts.
        var plan = new Plan(
            PlanId.Parse(d.PlanId),
            GoalId.Parse(d.GoalId),
            OwnerId.Parse(d.OwnerId),
            d.Version,
            d.Status,
            [.. d.Slots.Select(s => new PlanSlot(UtcTime.FromBson(s.Start), s.PowerW))],
            d.ExpectedCostEur,
            d.ArrivalCostEur,
            d.TargetMissed,
            d.PricesComplete,
            UtcTime.FromBson(d.MadeAt),
            UtcTime.FromBson(d.ExpiresAt));
        return new StoredPlan(
            plan,
            d.Reason,
            d.StartSocPercent,
            d.TargetSocPercent,
            d.EnergyNeededWh,
            UtcTime.FromBson(d.ConfirmedAt),
            d.ProposedTargetSocPercent,
            UtcTime.FromBson(d.ProposedDepartureAt));
    }
}
