using CheapHours.Host.Prices;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Prices;

public sealed class PriceImportScheduleTests
{
    private readonly PriceImportSchedule _schedule = new(new PricesOptions());

    [Fact]
    public void Start_at_nine_imports_today_but_not_tomorrow()
    {
        var clock = Clock(8); // 09:00 Berlin

        var now = clock.GetUtcNow();

        Assert.Equal(new DateOnly(2026, 11, 3), PriceImportSchedule.Today(now));
        Assert.False(_schedule.FetchesTomorrow(now));
        Assert.False(_schedule.PastGiveUp(now));
    }

    [Fact]
    public void Hourly_runs_reach_the_first_attempt_on_time()
    {
        var clock = Clock(8, 20); // 09:20 Berlin
        var runs = new List<DateTimeOffset>();
        for (var i = 0; i < 5; i++)
        {
            var next = _schedule.Next(clock.GetUtcNow());
            clock.SetUtcNow(next);
            runs.Add(next);
        }

        // 10:20, 11:20, 12:20 Berlin, then 13:00 sharp rather than 13:20, then hourly from there.
        Assert.Equal([At(9, 20), At(10, 20), At(11, 20), At(12, 0), At(13, 0)], runs);
        Assert.True(_schedule.FetchesTomorrow(At(12, 0)));
    }

    [Fact]
    public void First_tomorrow_attempt_is_within_a_minute_of_one_pm()
    {
        var first = _schedule.Next(At(11, 59, 30));

        Assert.Equal(At(12, 0), first); // 13:00 Berlin
        Assert.True(_schedule.FetchesTomorrow(first));
        Assert.False(_schedule.FetchesTomorrow(first.AddSeconds(-1)));
    }

    [Fact]
    public void Runs_retry_hourly_after_one_pm()
    {
        Assert.Equal(At(13, 0), _schedule.Next(At(12, 0)));
        Assert.Equal(At(14, 30), _schedule.Next(At(13, 30)));
    }

    [Fact]
    public void Tomorrow_is_not_fetched_from_eight_pm()
    {
        var eightPm = At(19, 0); // 20:00 Berlin

        Assert.True(_schedule.FetchesTomorrow(eightPm.AddSeconds(-1)));
        Assert.False(_schedule.FetchesTomorrow(eightPm));
        Assert.True(_schedule.PastGiveUp(eightPm));
        Assert.False(_schedule.FetchesTomorrow(At(22, 30))); // 23:30 Berlin
    }

    [Fact]
    public void After_midnight_the_new_day_is_today_and_the_give_up_resets()
    {
        var justAfterMidnight = At(23, 5); // 00:05 Berlin on 4 November

        Assert.Equal(new DateOnly(2026, 11, 4), PriceImportSchedule.Today(justAfterMidnight));
        Assert.Equal(new DateOnly(2026, 11, 5), PriceImportSchedule.Tomorrow(justAfterMidnight));
        Assert.False(_schedule.PastGiveUp(justAfterMidnight));
    }

    [Fact]
    public void Spring_change_day_starts_tomorrow_at_one_pm_summer_time()
    {
        // 29 March: 13:00 CEST is 11:00Z, 20:00 CEST is 18:00Z.
        var morning = new DateTimeOffset(2026, 3, 29, 9, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 10, 30, 0, TimeSpan.Zero), _schedule.Next(morning));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 11, 0, 0, TimeSpan.Zero), _schedule.Next(morning.AddHours(1)));
        Assert.True(_schedule.FetchesTomorrow(new DateTimeOffset(2026, 3, 29, 11, 0, 0, TimeSpan.Zero)));
        Assert.True(_schedule.PastGiveUp(new DateTimeOffset(2026, 3, 29, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 3, 30), PriceImportSchedule.Tomorrow(morning));
    }

    [Fact]
    public void Autumn_change_day_starts_tomorrow_at_one_pm_winter_time()
    {
        // 25 October: 13:00 CET is 12:00Z, 20:00 CET is 19:00Z; the local day has 25 hours.
        var night = new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero); // 00:30 CEST on 25 October

        Assert.Equal(new DateOnly(2026, 10, 25), PriceImportSchedule.Today(night));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), _schedule.Next(new DateTimeOffset(2026, 10, 25, 11, 30, 0, TimeSpan.Zero)));
        Assert.False(_schedule.FetchesTomorrow(new DateTimeOffset(2026, 10, 25, 11, 59, 0, TimeSpan.Zero)));
        Assert.True(_schedule.FetchesTomorrow(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(_schedule.PastGiveUp(new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero)));
    }

    // 2026-11-03 is a winter day: Berlin is UTC+1.
    private static FakeTimeProvider Clock(int hourUtc, int minute = 0) => new(At(hourUtc, minute));

    private static DateTimeOffset At(int hourUtc, int minute, int second = 0) => new(2026, 11, 3, hourUtc, minute, second, TimeSpan.Zero);
}
