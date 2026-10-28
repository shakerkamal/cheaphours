using System.Globalization;
using CheapHours.Core;
using CheapHours.Core.Devices;

namespace CheapHours.UnitTests.Core;

public sealed class TelemetryBucketTests
{
    private static readonly DateTimeOffset _ten = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bucket_derives_its_end_and_average_power()
    {
        var bucket = new TelemetryBucket(_ten, 2_750, 42m);

        Assert.Equal(_ten.AddMinutes(15), bucket.End);
        Assert.Equal(11_000, bucket.AvgPowerW);
        Assert.Equal(2_750, bucket.EnergyWh);
        Assert.Equal(42m, bucket.SocPercent);
    }

    [Fact]
    public void Bucket_without_state_of_charge_reports_it_unknown()
    {
        Assert.Null(new TelemetryBucket(_ten, 0).SocPercent);
    }

    [Fact]
    public void Start_off_the_grid_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TelemetryBucket(_ten.AddMinutes(7), 100));
    }

    [Fact]
    public void Non_utc_start_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TelemetryBucket(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(2)), 100));
    }

    [Fact]
    public void Negative_energy_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryBucket(_ten, -1));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void State_of_charge_out_of_range_is_refused(double soc)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryBucket(_ten, 100, (decimal)soc));
    }

    [Fact]
    public void Average_power_is_capped_for_impossible_meter_jumps()
    {
        Assert.Equal(int.MaxValue, new TelemetryBucket(_ten, long.MaxValue / 2).AvgPowerW);
    }

    [Theory]
    [InlineData("10:07", "10:00", "10:15")]
    [InlineData("10:52", "10:45", "11:00")]
    [InlineData("11:00", "11:00", "11:00")]
    public void Grid_rounds_down_and_up_to_whole_slots(string time, string floor, string ceiling)
    {
        Assert.Equal(At(floor), SlotGrid.Floor(At(time)));
        Assert.Equal(At(ceiling), SlotGrid.Ceiling(At(time)));
        Assert.Equal(time == floor, SlotGrid.IsAligned(At(time)));
    }

    private static DateTimeOffset At(string hhmm) =>
        new(2026, 10, 12, int.Parse(hhmm[..2], CultureInfo.InvariantCulture), int.Parse(hhmm[3..], CultureInfo.InvariantCulture), 0, TimeSpan.Zero);
}
