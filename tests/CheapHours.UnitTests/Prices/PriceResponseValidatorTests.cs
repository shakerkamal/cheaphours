using System.Text.Json;
using CheapHours.Host.Prices;

namespace CheapHours.UnitTests.Prices;

public sealed class PriceResponseValidatorTests
{
    private static readonly DateOnly Day = PriceFixtures.NormalDay;

    public static TheoryData<string, int> Fixtures => new()
    {
        { "2026-03-29", 92 },
        { "2026-10-25", 100 },
        { "2026-11-02", 96 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recorded_day_parses_to_its_slot_count_and_is_complete(string date, int slots)
    {
        var day = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);

        var parsed = PriceFixtures.Parsed(day);

        Assert.Equal(slots, parsed.SlotCount);
        Assert.Equal(slots, parsed.Slots.Count);
        Assert.True(parsed.IsComplete);
        Assert.Equal(BerlinTime.DayStartUtc(day), parsed.Slots[0].StartUtc);
        Assert.All(parsed.Slots, s => Assert.Equal("DE-LU", s.Zone));
    }

    [Fact]
    public void Recorded_daylight_saving_days_hold_negative_prices()
    {
        Assert.Contains(PriceFixtures.Parsed(PriceFixtures.SpringChange).Slots, s => s.PriceEurPerMwh < 0);
        Assert.Contains(PriceFixtures.Parsed(PriceFixtures.AutumnChange).Slots, s => s.PriceEurPerMwh < 0);
    }

    [Fact]
    public void Every_fixture_keeps_its_licence()
    {
        foreach (var day in PriceFixtures.Days)
        {
            using var json = JsonDocument.Parse(PriceFixtures.Raw(day));
            Assert.Contains("CC BY 4.0", json.RootElement.GetProperty("license_info").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Arrays_of_different_length_are_rejected()
    {
        var response = Synthetic(96) with { Price = [.. Enumerable.Repeat<decimal?>(80m, 95)] };

        Assert.Equal(new PriceValidation.Rejected(PriceRule.Lengths), Validate(response));
    }

    [Fact]
    public void Missing_arrays_are_rejected()
    {
        Assert.Equal(new PriceValidation.Rejected(PriceRule.Lengths), Validate(Synthetic(96) with { UnixSeconds = null }));
        Assert.Equal(new PriceValidation.Rejected(PriceRule.Lengths), Validate(Synthetic(96) with { Price = null }));
    }

    [Fact]
    public void Hourly_data_is_rejected()
    {
        var start = BerlinTime.DayStartUtc(Day).ToUnixTimeSeconds();
        var response = Synthetic(24) with { UnixSeconds = [.. Enumerable.Range(0, 24).Select(i => start + (i * 3600L))] };

        Assert.Equal(new PriceValidation.Rejected(PriceRule.Step), Validate(response));
    }

    [Fact]
    public void Price_out_of_bounds_is_rejected()
    {
        var prices = Enumerable.Repeat<decimal?>(80m, 96).ToArray();
        prices[40] = 9999m;

        Assert.Equal(new PriceValidation.Rejected(PriceRule.PriceBounds), Validate(Synthetic(96) with { Price = prices }));
    }

    [Theory]
    [InlineData(-500.01)]
    [InlineData(4000.01)]
    public void Prices_just_past_the_market_bounds_are_rejected(double price)
    {
        var prices = Enumerable.Repeat<decimal?>(80m, 96).ToArray();
        prices[0] = (decimal)price;

        Assert.Equal(new PriceValidation.Rejected(PriceRule.PriceBounds), Validate(Synthetic(96) with { Price = prices }));
    }

    [Theory]
    [InlineData("EUR / kWh")]
    [InlineData("")]
    [InlineData(null)]
    public void Wrong_unit_is_rejected(string? unit)
    {
        Assert.Equal(new PriceValidation.Rejected(PriceRule.Unit), Validate(Synthetic(96) with { Unit = unit }));
    }

    [Fact]
    public void Unit_spacing_does_not_matter()
    {
        Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(96) with { Unit = "EUR/MWh" }));
    }

    [Fact]
    public void Response_starting_after_the_first_slot_is_rejected()
    {
        var start = BerlinTime.DayStartUtc(Day).ToUnixTimeSeconds() + 900;
        var response = Synthetic(95) with { UnixSeconds = [.. Enumerable.Range(0, 95).Select(i => start + (i * 900L))] };

        Assert.Equal(new PriceValidation.Rejected(PriceRule.FirstSlot), Validate(response));
    }

    [Fact]
    public void Response_leaving_the_day_is_rejected()
    {
        Assert.Equal(new PriceValidation.Rejected(PriceRule.InsideDay), Validate(Synthetic(97)));
    }

    [Fact]
    public void Negative_prices_are_stored_as_they_are()
    {
        var prices = Enumerable.Repeat<decimal?>(-12.5m, 96).ToArray();

        var accepted = Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(96) with { Price = prices }));

        Assert.All(accepted.Day.Slots, s => Assert.Equal(-12.5m, s.PriceEurPerMwh));
    }

    [Fact]
    public void Day_ending_early_keeps_its_slots_and_is_not_complete()
    {
        var accepted = Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(48)));

        Assert.Equal(48, accepted.Day.Slots.Count);
        Assert.Equal(96, accepted.Day.SlotCount);
        Assert.False(accepted.Day.IsComplete);
    }

    [Fact]
    public void Null_prices_are_left_out_and_the_day_is_not_complete()
    {
        var prices = Enumerable.Repeat<decimal?>(80m, 96).ToArray();
        prices[3] = prices[50] = prices[51] = prices[95] = null;

        var accepted = Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(96) with { Price = prices }));

        Assert.Equal(92, accepted.Day.Slots.Count);
        Assert.False(accepted.Day.IsComplete);
        Assert.DoesNotContain(accepted.Day.Slots, s => s.StartUtc == BerlinTime.DayStartUtc(Day).AddMinutes(45));
    }

    [Fact]
    public void Empty_response_is_an_unpublished_day()
    {
        var accepted = Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(0)));

        Assert.Empty(accepted.Day.Slots);
        Assert.False(accepted.Day.IsComplete);
    }

    [Fact]
    public void Deprecated_flag_does_not_reject_the_response()
    {
        Assert.IsType<PriceValidation.Accepted>(Validate(Synthetic(96) with { Deprecated = true }));
    }

    private static PriceValidation Validate(EnergyChartsPriceResponse response) => PriceResponseValidator.Validate("DE-LU", Day, response);

    // A well-formed response for 2026-11-02 with the first `count` slots at 80 EUR/MWh.
    private static EnergyChartsPriceResponse Synthetic(int count)
    {
        var start = BerlinTime.DayStartUtc(Day).ToUnixTimeSeconds();
        return new EnergyChartsPriceResponse
        {
            LicenseInfo = "CC BY 4.0",
            UnixSeconds = [.. Enumerable.Range(0, count).Select(i => start + (i * 900L))],
            Price = [.. Enumerable.Repeat<decimal?>(80m, count)],
            Unit = "EUR / MWh",
        };
    }
}
