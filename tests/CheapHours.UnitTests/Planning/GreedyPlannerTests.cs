using CheapHours.Planning;

namespace CheapHours.UnitTests.Planning;

public sealed class GreedyPlannerTests
{
    private const int ElevenKw = 11_000;
    private static readonly DateTimeOffset Evening = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Energy_needed_comes_from_the_charge_gap_and_efficiency()
    {
        Assert.Equal(26_667, GreedyPlanner.EnergyNeededWh(40m, 80, 60_000, 0.90m));
    }

    [Fact]
    public void Already_at_target_gives_no_slots_and_no_missed_target()
    {
        var result = Plan(startSoc: 80m, target: 80, capacityWh: 60_000);

        Assert.Empty(result.Slots);
        Assert.False(result.TargetMissed);
        Assert.Equal(0, result.EnergyNeededWh);
        Assert.Equal(0m, result.ExpectedCostEur);
    }

    [Fact]
    public void Above_target_needs_no_energy()
    {
        Assert.Equal(0, GreedyPlanner.EnergyNeededWh(85m, 80, 60_000, 0.90m));
    }

    [Fact]
    public void Window_starts_at_the_next_slot_and_ends_before_the_margin()
    {
        var now = new DateTimeOffset(2026, 11, 2, 18, 7, 0, TimeSpan.Zero);
        var departure = new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero);

        var window = GreedyPlanner.Window(now, departure, TimeSpan.FromMinutes(30));

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 18, 15, 0, TimeSpan.Zero), window[0]);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 6, 30, 0, TimeSpan.Zero), window[^1].AddMinutes(15));
        Assert.Equal(49, window.Count);
    }

    [Fact]
    public void Night_across_the_autumn_change_holds_36_slots()
    {
        // 22:00 CEST on 24 October to 06:00 CET on 25 October: 8 hours on the wall clock, 9 hours of UTC.
        var now = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);
        var departure = new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero);

        Assert.Equal(36, GreedyPlanner.Window(now, departure, TimeSpan.Zero).Count);
    }

    [Fact]
    public void Departure_inside_the_margin_gives_an_empty_window()
    {
        Assert.Empty(GreedyPlanner.Window(Evening, Evening.AddMinutes(40), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Slot_count_covers_the_energy_at_full_power()
    {
        var result = Plan(startSoc: 40m, target: 80, capacityWh: 60_000, efficiency: 0.90m, hours: 12);

        Assert.Equal(10, result.Slots.Count);
        Assert.All(result.Slots, s => Assert.Equal(ElevenKw, s.PowerW));
        Assert.Equal(26_667, result.EnergyNeededWh);
    }

    [Fact]
    public void Night_slots_are_chosen_when_cheaper_and_listed_in_time_order()
    {
        // 18:00–22:00 evening at 300, 22:00–06:00 night at 80; 8 slots needed (22,000 Wh).
        var night = Evening.AddHours(4);
        var result = Plan(startSoc: 40m, target: 80, capacityWh: 55_000, efficiency: 1.0m, hours: 12,
            price: s => s < night ? 300m : 80m);

        Assert.Equal(8, result.Slots.Count);
        Assert.All(result.Slots, s => Assert.True(s.StartUtc >= night));
        Assert.Equal(result.Slots.OrderBy(s => s.StartUtc), result.Slots);
    }

    [Fact]
    public void Equal_prices_are_broken_by_the_earlier_slot()
    {
        // Everything at 100 except two later slots at 50; three slots needed: both 50s, then the earliest 100.
        var cheap1 = Evening.AddHours(2);
        var cheap2 = Evening.AddHours(3);
        var result = Plan(startSoc: 50m, target: 65, capacityWh: 55_000, efficiency: 1.0m, hours: 6,
            price: s => s == cheap1 || s == cheap2 ? 50m : 100m);

        Assert.Equal([Evening, cheap1, cheap2], result.Slots.Select(s => s.StartUtc));
    }

    [Fact]
    public void Negative_prices_are_chosen_first_and_lower_the_cost()
    {
        var negative = Evening.AddHours(3);
        var result = Plan(startSoc: 50m, target: 55, capacityWh: 55_000, efficiency: 1.0m, hours: 6,
            price: s => s == negative ? -12.5m : 40m);

        var slot = Assert.Single(result.Slots);
        Assert.Equal(negative, slot.StartUtc);
        Assert.Equal(ElevenKw * -12.5m / 4_000_000m, result.ExpectedCostEur);
        Assert.True(result.ExpectedCostEur < 0);
    }

    [Fact]
    public void Tomorrow_unpublished_uses_the_cheapest_priced_slots()
    {
        // 6 priced slots tonight, then 20 without a price; 4 slots needed.
        decimal[] tonight = [300m, 120m, 90m, 250m, 60m, 110m];
        var result = Plan(startSoc: 50m, target: 70, capacityWh: 55_000, efficiency: 1.0m, hours: 6.5,
            price: s => Index(s) < tonight.Length ? tonight[Index(s)] : null);

        Assert.Equal([Evening.AddMinutes(15), Evening.AddMinutes(30), Evening.AddMinutes(60), Evening.AddMinutes(75)],
            result.Slots.Select(s => s.StartUtc));
        Assert.True(result.PricesComplete);
        Assert.False(result.TargetMissed);
    }

    [Fact]
    public void Too_few_priced_slots_take_the_earliest_unpriced_and_flag_incomplete_prices()
    {
        // 10 slots needed; only the first 6 are priced.
        var result = Plan(startSoc: 40m, target: 90, capacityWh: 55_000, efficiency: 1.0m, hours: 6,
            price: s => Index(s) < 6 ? 100m : null);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => Evening.AddMinutes(15 * i)), result.Slots.Select(s => s.StartUtc));
        Assert.False(result.PricesComplete);
    }

    [Fact]
    public void Short_window_takes_every_slot_and_flags_the_target_missed()
    {
        // 10 slots needed, 6 in the window.
        var result = Plan(startSoc: 40m, target: 90, capacityWh: 55_000, efficiency: 1.0m, hours: 1.5);

        Assert.Equal(6, result.Slots.Count);
        Assert.True(result.TargetMissed);
    }

    [Fact]
    public void Enough_time_does_not_flag_the_target()
    {
        var result = Plan(startSoc: 40m, target: 90, capacityWh: 55_000, efficiency: 1.0m, hours: 2.5);

        Assert.Equal(10, result.Slots.Count);
        Assert.False(result.TargetMissed);
    }

    [Fact]
    public void Costs_are_summed_over_chosen_and_arrival_slots()
    {
        // Two slots needed (5,500 Wh). The window's first two cost 300 and 280; two later ones 80 and 100.
        decimal[] prices = [300m, 280m, 200m, 80m, 100m, 150m];
        var result = Plan(startSoc: 40m, target: 50, capacityWh: 55_000, efficiency: 1.0m, hours: 1.5,
            price: s => prices[Index(s)]);

        Assert.Equal(2, result.Slots.Count);
        Assert.Equal(0.495m, result.ExpectedCostEur);
        Assert.Equal(1.595m, result.ArrivalCostEur);
    }

    [Fact]
    public void Unpriced_slots_add_nothing_to_either_cost()
    {
        var result = Plan(startSoc: 40m, target: 50, capacityWh: 55_000, efficiency: 1.0m, hours: 1.5, price: _ => null);

        Assert.Equal(2, result.Slots.Count);
        Assert.Equal(0m, result.ExpectedCostEur);
        Assert.Equal(0m, result.ArrivalCostEur);
        Assert.False(result.PricesComplete);
    }

    [Fact]
    public void Same_inputs_give_equal_plans()
    {
        Func<DateTimeOffset, decimal?> price = s => (s.Minute * 7 % 13) + s.Hour;
        var first = Plan(startSoc: 33.5m, target: 77, capacityWh: 64_000, hours: 10, price: price);
        var second = Plan(startSoc: 33.5m, target: 77, capacityWh: 64_000, hours: 10, price: price);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void A_different_slot_makes_plans_unequal()
    {
        var first = Plan(startSoc: 40m, target: 50, capacityWh: 55_000, efficiency: 1.0m, hours: 2, price: _ => 100m);
        var second = Plan(startSoc: 40m, target: 50, capacityWh: 55_000, efficiency: 1.0m, hours: 2,
            price: s => s == Evening.AddHours(1) ? 10m : 100m);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Input_must_be_utc_and_in_range()
    {
        var prices = new Dictionary<DateTimeOffset, decimal>();
        var local = new DateTimeOffset(2026, 11, 2, 19, 0, 0, TimeSpan.FromHours(1));

        Assert.Throws<ArgumentException>(() => new PlanningInput(local, Evening.AddHours(5), 40m, 80, 60_000, ElevenKw, prices, TimeSpan.Zero, 0.9m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanningInput(Evening, Evening.AddHours(5), 40m, 80, 60_000, 0, prices, TimeSpan.Zero, 0.9m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanningInput(Evening, Evening.AddHours(5), 40m, 80, 60_000, ElevenKw, prices, TimeSpan.Zero, 0.4m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanningInput(Evening, Evening.AddHours(5), 101m, 80, 60_000, ElevenKw, prices, TimeSpan.Zero, 0.9m));
    }

    private static int Index(DateTimeOffset slot) => (int)((slot - Evening).Ticks / TimeSpan.FromMinutes(15).Ticks);

    // Plans from 18:00Z with no margin over a window of the given hours; price returns null for "no price".
    private static PlanningResult Plan(
        decimal startSoc,
        int target,
        long capacityWh,
        decimal efficiency = 0.90m,
        double hours = 12,
        Func<DateTimeOffset, decimal?>? price = null)
    {
        var departure = Evening.AddHours(hours);
        var prices = new Dictionary<DateTimeOffset, decimal>();
        for (var s = Evening; s < departure; s = s.AddMinutes(15))
        {
            if ((price ?? (_ => 100m))(s) is { } p)
            {
                prices[s] = p;
            }
        }

        return GreedyPlanner.Plan(new PlanningInput(Evening, departure, startSoc, target, capacityWh, ElevenKw, prices, TimeSpan.Zero, efficiency));
    }
}
