using CheapHours.Host.Prices;

namespace CheapHours.UnitTests.Prices;

public sealed class BerlinTimeTests
{
    [Fact]
    public void Spring_change_day_has_92_slots()
    {
        var day = new DateOnly(2026, 3, 29);

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), BerlinTime.DayStartUtc(day));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero), BerlinTime.DayEndUtc(day));
        Assert.Equal(92, BerlinTime.SlotCount(day));
    }

    [Fact]
    public void Autumn_change_day_has_100_slots()
    {
        var day = new DateOnly(2026, 10, 25);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), BerlinTime.DayStartUtc(day));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), BerlinTime.DayEndUtc(day));
        Assert.Equal(100, BerlinTime.SlotCount(day));
    }

    [Theory]
    [InlineData("2026-11-02")]
    [InlineData("2026-07-01")]
    [InlineData("2026-03-28")]
    [InlineData("2026-10-26")]
    public void Other_days_have_96_slots(string date)
    {
        Assert.Equal(96, BerlinTime.SlotCount(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Day_of_a_utc_time_is_its_local_day()
    {
        Assert.Equal(new DateOnly(2026, 11, 2), BerlinTime.DayOf(new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 1), BerlinTime.DayOf(new DateTimeOffset(2026, 11, 1, 22, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 7, 2), BerlinTime.DayOf(new DateTimeOffset(2026, 7, 1, 22, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Local_time_skipped_in_spring_is_the_moment_the_clock_jumps()
    {
        // 02:30 does not exist on 29 March: it counts as 03:00 CEST, 01:00Z.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), BerlinTime.AtLocal(new DateOnly(2026, 3, 29), new TimeOnly(2, 30)));
    }

    [Fact]
    public void Repeated_local_time_in_autumn_is_its_first_pass()
    {
        // 02:30 happens twice on 25 October: first at +02:00 (00:30Z), then at +01:00 (01:30Z).
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), BerlinTime.AtLocal(new DateOnly(2026, 10, 25), new TimeOnly(2, 30)));
    }

    [Fact]
    public void Local_time_converts_with_the_season_offset()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), BerlinTime.AtLocal(new DateOnly(2026, 11, 2), new TimeOnly(13, 0)));
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero), BerlinTime.AtLocal(new DateOnly(2026, 7, 1), new TimeOnly(13, 0)));
    }
}
