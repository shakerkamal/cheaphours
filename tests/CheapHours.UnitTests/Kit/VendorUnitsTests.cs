using CheapHours.Providers.Kit;

namespace CheapHours.UnitTests.Kit;

public sealed class VendorUnitsTests
{
    [Fact]
    public void Conversions()
    {
        Assert.Equal(11_000, VendorUnits.KilowattsToWatts(11m));
        Assert.Equal(3_251, VendorUnits.KilowattHoursToWattHours(3.2505m));
        Assert.True(VendorUnits.TryParseUtc("2026-10-28T19:00:00+01:00", out var utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
    }

    [Theory]
    [InlineData("0.0005", 1)]
    [InlineData("-0.0005", -1)]
    [InlineData("0.0004", 0)]
    [InlineData("7.4", 7_400)]
    public void Kilowatts_round_halves_away_from_zero(string kilowatts, int watts)
    {
        Assert.Equal(watts, VendorUnits.KilowattsToWatts(decimal.Parse(kilowatts, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("2026-10-28T18:00:00Z")]
    [InlineData("2026-10-28T18:00:00.123Z")]
    [InlineData("2026-10-28T20:00:00+02:00")]
    public void Timestamps_with_an_offset_are_read_as_utc(string text)
    {
        Assert.True(VendorUnits.TryParseUtc(text, out var utc));
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(new DateTime(2026, 10, 28, 18, 0, 0), utc.UtcDateTime.AddTicks(-(utc.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Theory]
    [InlineData("2026-10-28T18:00:00")]
    [InlineData("2026-10-28 18:00:00Z")]
    [InlineData("28.10.2026 18:00")]
    [InlineData("")]
    [InlineData(null)]
    public void Timestamp_without_offset_is_unusable(string? text)
    {
        Assert.False(VendorUnits.TryParseUtc(text, out _));
    }
}
