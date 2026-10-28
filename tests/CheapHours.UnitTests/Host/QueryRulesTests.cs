using CheapHours.Core.Ids;
using CheapHours.Host.Api;

namespace CheapHours.UnitTests.Host;

public sealed class QueryRulesTests
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 50, true)]
    [InlineData(1, 1, true)]
    [InlineData(200, 200, true)]
    [InlineData(0, 0, false)]
    [InlineData(-5, -5, false)]
    [InlineData(201, 201, false)]
    [InlineData(500, 500, false)]
    public void Limit_defaults_to_50_and_must_be_1_to_200(int? sent, int used, bool valid)
    {
        var rules = new QueryRules();

        Assert.Equal(used, rules.Limit(sent));
        Assert.Equal(valid, rules.IsValid);
        Assert.Equal(!valid, rules.Errors.ContainsKey("limit"));
    }

    [Theory]
    [InlineData(0, 60, null)]
    [InlineData(0, 31 * 24 * 60, null)] // exactly 31 days
    [InlineData(0, (31 * 24 * 60) + 1, "to")]
    [InlineData(60, 60, "to")]
    [InlineData(60, 0, "to")]
    public void Range_must_be_ordered_and_at_most_31_days(int fromMinute, int toMinute, string? invalid)
    {
        var rules = new QueryRules();

        rules.Range(Ten.AddMinutes(fromMinute), Ten.AddMinutes(toMinute));

        Assert.Equal(invalid is null, rules.IsValid);
        if (invalid is not null)
        {
            Assert.True(rules.Errors.ContainsKey(invalid));
        }
    }

    [Fact]
    public void Range_with_an_offset_is_refused_per_parameter()
    {
        var rules = new QueryRules();
        var berlin = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2));

        rules.Range(berlin, berlin.AddHours(1));

        Assert.Equal(["from", "to"], rules.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("15m", true)]
    [InlineData("1h", false)]
    [InlineData("15M", false)]
    [InlineData("", false)]
    public void Resolution_must_be_absent_or_15m(string? resolution, bool valid)
    {
        var rules = new QueryRules();

        rules.Resolution(resolution);

        Assert.Equal(valid, rules.IsValid);
        Assert.Equal(!valid, rules.Errors.ContainsKey("resolution"));
    }

    [Fact]
    public void Device_cursor_round_trips_and_garbage_is_refused()
    {
        var id = DeviceId.Parse("dev_01JB7Z8K2Q");
        var rules = new QueryRules();

        Assert.Null(rules.DeviceCursor(null));
        Assert.Equal(id, rules.DeviceCursor(PageCursor.ForDevice(id)));
        Assert.True(rules.IsValid);

        rules.DeviceCursor("not-a-cursor");
        Assert.True(rules.Errors.ContainsKey("cursor"));
    }

    [Fact]
    public void Telemetry_cursor_continues_at_the_next_slot()
    {
        var rules = new QueryRules();

        var next = rules.TelemetryCursor(PageCursor.ForBucket(Ten.AddMinutes(15)), Ten, Ten.AddHours(1));

        Assert.Equal(Ten.AddMinutes(30), next);
        Assert.True(rules.IsValid);
    }

    [Theory]
    [InlineData(-15)] // before the range
    [InlineData(60)] // at the end of the range
    [InlineData(120)] // after the range
    public void Telemetry_cursor_outside_the_range_is_refused(int minute)
    {
        var rules = new QueryRules();

        Assert.Equal(Ten, rules.TelemetryCursor(PageCursor.ForBucket(Ten.AddMinutes(minute)), Ten, Ten.AddHours(1)));
        Assert.True(rules.Errors.ContainsKey("cursor"));
    }

    [Fact]
    public void Device_cursor_is_not_a_telemetry_cursor()
    {
        var rules = new QueryRules();

        rules.TelemetryCursor(PageCursor.ForDevice(DeviceId.Parse("dev_1")), Ten, Ten.AddHours(1));

        Assert.True(rules.Errors.ContainsKey("cursor"));
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var rules = new QueryRules();

        rules.Limit(0);
        rules.Resolution("1h");
        rules.DeviceCursor("x");

        Assert.Equal(["cursor", "limit", "resolution"], rules.Errors.Keys.Order(StringComparer.Ordinal));
    }
}
