using CheapHours.Core;
using CheapHours.Core.Goals;

namespace CheapHours.Planning;

/// <summary>
/// Chooses the cheapest 15-minute slots before departure (ADR-005). Every slot charges at the charger's maximum
/// power, so each gives the same energy and the cheapest <c>n</c> slots of the window are the cheapest plan.
/// Pure: no clock, no I/O, decimals only.
/// </summary>
public static class GreedyPlanner
{
    private const decimal WattQuarterHoursPerMwh = 4_000_000m;

    /// <summary>Plans one charging goal.</summary>
    /// <param name="input">The planning input.</param>
    /// <returns>The chosen slots, costs and flags.</returns>
    public static PlanningResult Plan(PlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var energyNeededWh = EnergyNeededWh(input.StartSocPercent, input.TargetSocPercent, input.BatteryCapacityWh, input.ChargingEfficiency);
        var window = Window(input.NowUtc, input.DepartureUtc, input.SafetyMargin);
        var power = input.ChargerMaxPowerW;

        // n = ceil(energy / (power / 4)), in quarter-hour Wh so no power needs to be divisible by 4.
        var needed = (int)Math.Min(int.MaxValue, ((energyNeededWh * 4) + power - 1) / power);
        var targetMissed = needed > window.Count;
        var count = Math.Min(needed, window.Count);

        bool Priced(DateTimeOffset start) => input.PricesEurPerMwh.ContainsKey(start);

        IEnumerable<DateTimeOffset> chosen;
        if (count == window.Count)
        {
            chosen = window;
        }
        else
        {
            var cheapest = window.Where(Priced)
                .OrderBy(s => input.PricesEurPerMwh[s])
                .ThenBy(s => s)
                .Take(count)
                .ToList();
            var unpriced = window.Where(s => !Priced(s)).Take(count - cheapest.Count);
            chosen = cheapest.Concat(unpriced);
        }

        var slots = chosen.Order().Select(s => new PlanSlot(s, power)).ToList();
        var arrival = window.Take(count).ToList();

        return new PlanningResult(
            slots,
            Cost(slots.Select(s => s.StartUtc), power, input.PricesEurPerMwh),
            Cost(arrival, power, input.PricesEurPerMwh),
            targetMissed,
            slots.All(s => Priced(s.StartUtc)) && arrival.All(Priced),
            energyNeededWh);
    }

    /// <summary>
    /// Returns the grid energy needed to lift the charge from <paramref name="startSocPercent"/> to
    /// <paramref name="targetSocPercent"/>, rounded up to a whole Wh; zero at or above the target.
    /// </summary>
    /// <param name="startSocPercent">The current charge in percent.</param>
    /// <param name="targetSocPercent">The target charge in percent.</param>
    /// <param name="batteryCapacityWh">The battery capacity in Wh.</param>
    /// <param name="chargingEfficiency">The share of grid energy that reaches the battery.</param>
    /// <returns>The energy in Wh.</returns>
    public static long EnergyNeededWh(decimal startSocPercent, int targetSocPercent, long batteryCapacityWh, decimal chargingEfficiency)
    {
        var gap = Math.Max(0m, targetSocPercent - startSocPercent);
        return (long)Math.Ceiling(gap / 100m * batteryCapacityWh / chargingEfficiency);
    }

    /// <summary>
    /// Returns the whole slots that start at or after <paramref name="nowUtc"/> and end at or before departure
    /// minus the margin, by stepping 15 minutes in UTC (never by local hours).
    /// </summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <param name="departureUtc">The departure (UTC).</param>
    /// <param name="safetyMargin">The safety margin.</param>
    /// <returns>The slot starts in time order; empty when no whole slot fits.</returns>
    public static IReadOnlyList<DateTimeOffset> Window(DateTimeOffset nowUtc, DateTimeOffset departureUtc, TimeSpan safetyMargin)
    {
        var first = SlotGrid.Ceiling(nowUtc);
        var end = SlotGrid.Floor(departureUtc - safetyMargin);
        var slots = new List<DateTimeOffset>();
        for (var start = first; start + SlotGrid.SlotLength <= end; start += SlotGrid.SlotLength)
        {
            slots.Add(start);
        }

        return slots;
    }

    private static decimal Cost(IEnumerable<DateTimeOffset> starts, int powerW, IReadOnlyDictionary<DateTimeOffset, decimal> prices) =>
        starts.Sum(s => prices.TryGetValue(s, out var price) ? powerW * price / WattQuarterHoursPerMwh : 0m);
}
