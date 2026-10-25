using CheapHours.Core.Devices;

namespace CheapHours.UnitTests.Core;

public sealed class DevicePresenceTests
{
    private static readonly DateTimeOffset LastSeen = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(179, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    [InlineData(196, false)]
    public void Online_until_more_than_three_intervals_have_passed(int secondsSince, bool online)
    {
        Assert.Equal(online, DevicePresence.IsOnline(LastSeen, Interval, LastSeen.AddSeconds(secondsSince)));
    }

    [Fact]
    public void Three_missed_intervals_are_allowed()
    {
        Assert.Equal(3, DevicePresence.MissedIntervalsAllowed);
    }

    [Fact]
    public void Exactly_three_intervals_plus_a_tick_is_offline()
    {
        Assert.False(DevicePresence.IsOnline(LastSeen, Interval, LastSeen + (3 * Interval) + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void A_clock_behind_the_last_frame_counts_as_online()
    {
        Assert.True(DevicePresence.IsOnline(LastSeen, Interval, LastSeen.AddSeconds(-5)));
    }

    [Fact]
    public void Non_utc_times_are_rejected()
    {
        var local = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => DevicePresence.IsOnline(local, Interval, LastSeen));
        Assert.Throws<ArgumentException>(() => DevicePresence.IsOnline(LastSeen, Interval, local));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Non_positive_interval_is_rejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DevicePresence.IsOnline(LastSeen, TimeSpan.FromSeconds(seconds), LastSeen));
    }
}
