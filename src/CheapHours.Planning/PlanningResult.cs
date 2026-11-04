using CheapHours.Core.Goals;

namespace CheapHours.Planning;

/// <summary>
/// What the planner chose: the slots in time order, the two costs (unrounded EUR) and the two flags. Two
/// results are equal when every one of these is equal, so a re-plan can tell whether anything changed.
/// </summary>
/// <param name="Slots">The chosen slots in time order, each at the charger's maximum power.</param>
/// <param name="ExpectedCostEur">The summed cost of the chosen priced slots, in EUR.</param>
/// <param name="ArrivalCostEur">The summed cost of the arrival slots (the window's first slots), in EUR.</param>
/// <param name="TargetMissed">Whether the window holds fewer slots than needed.</param>
/// <param name="PricesComplete">Whether every chosen and every arrival slot had a price.</param>
/// <param name="EnergyNeededWh">The grid energy the plan is meant to deliver, in Wh.</param>
public sealed record PlanningResult(
    IReadOnlyList<PlanSlot> Slots,
    decimal ExpectedCostEur,
    decimal ArrivalCostEur,
    bool TargetMissed,
    bool PricesComplete,
    long EnergyNeededWh)
{
    /// <inheritdoc/>
    public bool Equals(PlanningResult? other) =>
        other is not null
        && Slots.SequenceEqual(other.Slots)
        && ExpectedCostEur == other.ExpectedCostEur
        && ArrivalCostEur == other.ArrivalCostEur
        && TargetMissed == other.TargetMissed
        && PricesComplete == other.PricesComplete
        && EnergyNeededWh == other.EnergyNeededWh;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var slot in Slots)
        {
            hash.Add(slot);
        }

        hash.Add(ExpectedCostEur);
        hash.Add(ArrivalCostEur);
        hash.Add(TargetMissed);
        hash.Add(PricesComplete);
        hash.Add(EnergyNeededWh);
        return hash.ToHashCode();
    }
}
