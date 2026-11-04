using CheapHours.Core;
using CheapHours.Core.Goals;

namespace CheapHours.Planning;

/// <summary>
/// How far a plan should have got at a given time, for the drift check. It uses only what a stored plan
/// version records (slots, start and target charge, planned energy), so it needs no capacity or efficiency.
/// </summary>
public static class PlanProgress
{
    /// <summary>
    /// Returns the charge the plan expects at <paramref name="atUtc"/>: the start charge plus the share of the
    /// planned energy delivered before that time (the current slot pro rata), capped at the target.
    /// </summary>
    /// <param name="slots">The plan's slots.</param>
    /// <param name="startSocPercent">The charge the plan started from.</param>
    /// <param name="targetSocPercent">The target charge.</param>
    /// <param name="energyNeededWh">The grid energy the plan was made for.</param>
    /// <param name="atUtc">The time to evaluate (UTC).</param>
    /// <returns>The expected charge in percent.</returns>
    public static decimal ExpectedSoc(
        IReadOnlyList<PlanSlot> slots,
        decimal startSocPercent,
        int targetSocPercent,
        long energyNeededWh,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (energyNeededWh <= 0 || targetSocPercent <= startSocPercent)
        {
            return startSocPercent;
        }

        var delivered = DeliveredWh(slots, atUtc);
        var share = Math.Min(1m, delivered / energyNeededWh);
        return startSocPercent + (share * (targetSocPercent - startSocPercent));
    }

    /// <summary>Returns the grid energy the plan delivers before <paramref name="atUtc"/>, in Wh.</summary>
    /// <param name="slots">The plan's slots.</param>
    /// <param name="atUtc">The time to evaluate (UTC).</param>
    /// <returns>The energy in Wh.</returns>
    public static decimal DeliveredWh(IReadOnlyList<PlanSlot> slots, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var delivered = 0m;
        foreach (var slot in slots)
        {
            var elapsed = Math.Clamp((atUtc - slot.StartUtc).Ticks, 0, SlotGrid.SlotLength.Ticks);
            delivered += slot.PowerW * (decimal)elapsed / TimeSpan.TicksPerHour;
        }

        return delivered;
    }
}
