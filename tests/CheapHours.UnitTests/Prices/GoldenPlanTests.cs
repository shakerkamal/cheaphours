using CheapHours.Planning;

namespace CheapHours.UnitTests.Prices;

// The gate's golden test: a goal planned against the recorded 2026-11-02 DE-LU prices. The expected slots and
// costs were worked out by hand from the fixture once (the 10 cheapest of the 56 slots from 08:00Z to 22:00Z;
// the 11th cheapest, 10:30Z at 215.42, is dearer than the 10th, 13:15Z at 214.70, so there is no tie) and are
// pinned here.
public sealed class GoldenPlanTests
{
    private static readonly DateTimeOffset MadeAt = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Departure = new(2026, 11, 2, 22, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Goal_on_the_recorded_day_gives_the_pinned_plan()
    {
        var options = new PlannerOptions();
        var prices = PriceFixtures.Parsed(PriceFixtures.NormalDay).Slots.ToDictionary(s => s.StartUtc, s => s.PriceEurPerMwh);

        var result = GreedyPlanner.Plan(new PlanningInput(
            MadeAt, Departure, startSocPercent: 40m, targetSocPercent: 80, batteryCapacityWh: 60_000, chargerMaxPowerW: 11_000,
            prices, options.SafetyMargin, options.ChargingEfficiency));

        string[] expected = ["10:15", "11:15", "11:30", "11:45", "12:15", "12:30", "12:45", "13:00", "13:15", "13:30"];
        Assert.Equal(expected, result.Slots.Select(s => s.StartUtc.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(result.Slots, s => Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(s.StartUtc.UtcDateTime)));
        Assert.All(result.Slots, s => Assert.Equal(11_000, s.PowerW));
        Assert.Equal(26_667, result.EnergyNeededWh);
        Assert.Equal(5.738425m, result.ExpectedCostEur);
        Assert.Equal(7.0664m, result.ArrivalCostEur);
        Assert.False(result.TargetMissed);
        Assert.True(result.PricesComplete);
    }
}
