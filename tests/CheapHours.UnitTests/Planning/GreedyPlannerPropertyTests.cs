using CheapHours.Planning;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CheapHours.UnitTests.Planning;

// The four rules every plan satisfies (charging-planner spec), over random times, charges, batteries, chargers
// and prices. Windows start around both 2026 daylight-saving changes and a normal day; prices include negative
// and missing ones.
public sealed class GreedyPlannerPropertyTests
{
    private const int Cases = 1_000;

    [Property(MaxTest = Cases, Arbitrary = [typeof(PlannerArbitraries)])]
    public void No_slot_ends_after_departure_minus_the_margin(PlannerScenario scenario)
    {
        var input = scenario.ToInput();
        var result = GreedyPlanner.Plan(input);

        Assert.All(result.Slots, s =>
        {
            Assert.True(s.EndUtc <= input.DepartureUtc - input.SafetyMargin, $"slot {s.StartUtc:O} ends past the margin");
            Assert.True(s.StartUtc >= input.NowUtc, $"slot {s.StartUtc:O} starts before now");
        });
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(PlannerArbitraries)])]
    public void No_slot_charges_above_the_charger_power(PlannerScenario scenario)
    {
        var input = scenario.ToInput();

        Assert.All(GreedyPlanner.Plan(input).Slots, s => Assert.InRange(s.PowerW, 1, input.ChargerMaxPowerW));
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(PlannerArbitraries)])]
    public void Energy_is_covered_whenever_the_window_allows_it(PlannerScenario scenario)
    {
        var input = scenario.ToInput();
        var result = GreedyPlanner.Plan(input);
        var window = GreedyPlanner.Window(input.NowUtc, input.DepartureUtc, input.SafetyMargin);
        var slotWh = input.ChargerMaxPowerW / 4m;
        var neededSlots = (int)Math.Ceiling(result.EnergyNeededWh / slotWh);

        if (window.Count >= neededSlots)
        {
            var planned = result.Slots.Count * slotWh;
            Assert.False(result.TargetMissed);
            Assert.True(planned >= result.EnergyNeededWh, $"plans {planned} Wh of {result.EnergyNeededWh}");
            Assert.True(planned < result.EnergyNeededWh + slotWh, $"plans {planned} Wh, more than one slot over {result.EnergyNeededWh}");
        }
        else
        {
            Assert.True(result.TargetMissed);
            Assert.Equal(window.Count, result.Slots.Count);
        }
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(PlannerArbitraries)])]
    public void Fully_priced_plan_never_costs_more_than_charging_on_arrival(PlannerScenario scenario)
    {
        var input = scenario.WithEveryPrice().ToInput();
        var result = GreedyPlanner.Plan(input);

        Assert.True(result.PricesComplete);
        Assert.True(result.ExpectedCostEur <= result.ArrivalCostEur, $"{result.ExpectedCostEur} > {result.ArrivalCostEur}");
    }
}

/// <summary>Missing prices: none, a random tenth, or everything after a cut-off (tomorrow not published).</summary>
public enum MissingPrices
{
    None,
    Scattered,
    AfterCutOff,
}

/// <summary>A generated planning case. Prices come from a seeded random so they are reproducible from the case.</summary>
public sealed record PlannerScenario(
    DateTimeOffset NowUtc,
    int DepartureAfterMinutes,
    int MarginMinutes,
    decimal StartSocPercent,
    int TargetSocPercent,
    long BatteryCapacityWh,
    int ChargerMaxPowerW,
    decimal ChargingEfficiency,
    int PriceSeed,
    MissingPrices Missing)
{
    public PlannerScenario WithEveryPrice() => this with { Missing = MissingPrices.None };

    public PlanningInput ToInput()
    {
        var departure = NowUtc.AddMinutes(DepartureAfterMinutes);
        var random = new Random(PriceSeed);
        var cutOff = NowUtc.AddMinutes(random.Next(0, Math.Max(1, DepartureAfterMinutes)));
        var prices = new Dictionary<DateTimeOffset, decimal>();
        var start = CheapHours.Core.SlotGrid.Floor(NowUtc);
        for (var s = start; s < departure; s = s.AddMinutes(15))
        {
            var missing = Missing switch
            {
                MissingPrices.Scattered => random.Next(10) == 0,
                MissingPrices.AfterCutOff => s >= cutOff,
                _ => false,
            };

            // Mostly 0–400 EUR/MWh, sometimes negative, rarely near the market bounds; two decimals.
            var price = random.Next(20) switch
            {
                0 => random.Next(-50_000, 0) / 100m,
                1 => random.Next(40_000, 400_000) / 100m,
                _ => random.Next(0, 40_000) / 100m,
            };

            if (!missing)
            {
                prices[s] = price;
            }
        }

        return new PlanningInput(
            NowUtc, departure, StartSocPercent, TargetSocPercent, BatteryCapacityWh, ChargerMaxPowerW, prices,
            TimeSpan.FromMinutes(MarginMinutes), ChargingEfficiency);
    }

    public override string ToString() =>
        $"now {NowUtc:O}, departure +{DepartureAfterMinutes} min, margin {MarginMinutes} min, {StartSocPercent} % → {TargetSocPercent} %, " +
        $"{BatteryCapacityWh} Wh, {ChargerMaxPowerW} W, efficiency {ChargingEfficiency}, seed {PriceSeed}, missing {Missing}";
}

public static class PlannerArbitraries
{
    // Evenings before the spring change (2026-03-29 01:00Z) and the autumn change (2026-10-25 01:00Z), and a
    // normal day, so generated windows regularly cross a 23- or 25-hour local day.
    private static readonly DateTimeOffset[] Anchors =
    [
        new(2026, 3, 28, 16, 0, 0, TimeSpan.Zero),
        new(2026, 10, 24, 16, 0, 0, TimeSpan.Zero),
        new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero),
    ];

    public static Arbitrary<PlannerScenario> Scenario()
    {
        var gen =
            from anchor in Gen.Elements(Anchors)
            from offsetSeconds in Gen.Choose(0, 12 * 3600)
            from departureAfter in Gen.Choose(0, 48 * 60)
            from margin in Gen.Choose(0, 240)
            from startTenths in Gen.Choose(0, 1000)
            from target in Gen.Choose(1, 100)
            from capacity in Gen.Choose(10_000, 120_000)
            from power in Gen.OneOf(Gen.Elements(1_380, 3_700, 7_400, 11_000, 22_000), Gen.Choose(1_000, 50_000))
            from efficiency in Gen.Choose(50, 100)
            from seed in Gen.Choose(int.MinValue, int.MaxValue)
            from missing in Gen.Elements(MissingPrices.None, MissingPrices.Scattered, MissingPrices.AfterCutOff)
            select new PlannerScenario(
                anchor.AddSeconds(offsetSeconds), departureAfter, margin, startTenths / 10m, target, capacity, power,
                efficiency / 100m, seed, missing);

        return gen.ToArbitrary();
    }
}
