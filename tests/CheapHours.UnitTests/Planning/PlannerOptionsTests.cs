using CheapHours.Planning;

namespace CheapHours.UnitTests.Planning;

public sealed class PlannerOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new PlannerOptions();

        Assert.Empty(PlannerOptionsValidator.Validate(options));
        Assert.Equal(TimeSpan.FromMinutes(30), options.SafetyMargin);
        Assert.Equal(0.90m, options.ChargingEfficiency);
        Assert.Equal(TimeSpan.FromMinutes(10), options.DraftExpiry);
        Assert.Equal(TimeSpan.FromHours(48), options.MaxHorizon);
        Assert.Equal(5m, options.DriftThresholdPercent);
        Assert.Equal(TimeSpan.FromMinutes(5), options.DriftCheckInterval);
    }

    public static TheoryData<Action<PlannerOptions>, string> OutOfRange => new()
    {
        { o => o.SafetyMargin = TimeSpan.FromMinutes(-1), "SafetyMargin" },
        { o => o.SafetyMargin = TimeSpan.FromHours(4.5), "SafetyMargin" },
        { o => o.ChargingEfficiency = 0.49m, "ChargingEfficiency" },
        { o => o.ChargingEfficiency = 1.01m, "ChargingEfficiency" },
        { o => o.DraftExpiry = TimeSpan.FromSeconds(30), "DraftExpiry" },
        { o => o.DraftExpiry = TimeSpan.FromMinutes(61), "DraftExpiry" },
        { o => o.MaxHorizon = TimeSpan.FromMinutes(30), "MaxHorizon" },
        { o => o.MaxHorizon = TimeSpan.FromDays(8), "MaxHorizon" },
        { o => o.DriftThresholdPercent = 0.5m, "DriftThresholdPercent" },
        { o => o.DriftThresholdPercent = 51m, "DriftThresholdPercent" },
        { o => o.DriftCheckInterval = TimeSpan.FromSeconds(10), "DriftCheckInterval" },
        { o => o.DriftCheckInterval = TimeSpan.FromHours(2), "DriftCheckInterval" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Out_of_range_value_is_named_by_key(Action<PlannerOptions> change, string key)
    {
        var options = new PlannerOptions();
        change(options);

        var problem = Assert.Single(PlannerOptionsValidator.Validate(options));
        Assert.StartsWith("Planner: " + key, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_never_contain_the_value()
    {
        var options = new PlannerOptions { ChargingEfficiency = 0.123m };

        Assert.DoesNotContain("0.123", Assert.Single(PlannerOptionsValidator.Validate(options)), StringComparison.Ordinal);
    }

    [Fact]
    public void Edges_of_each_range_are_valid()
    {
        var low = new PlannerOptions
        {
            SafetyMargin = TimeSpan.Zero,
            ChargingEfficiency = 0.5m,
            DraftExpiry = TimeSpan.FromMinutes(1),
            MaxHorizon = TimeSpan.FromHours(1),
            DriftThresholdPercent = 1m,
            DriftCheckInterval = TimeSpan.FromSeconds(30),
        };
        var high = new PlannerOptions
        {
            SafetyMargin = TimeSpan.FromHours(4),
            ChargingEfficiency = 1.0m,
            DraftExpiry = TimeSpan.FromMinutes(60),
            MaxHorizon = TimeSpan.FromDays(7),
            DriftThresholdPercent = 50m,
            DriftCheckInterval = TimeSpan.FromHours(1),
        };

        Assert.Empty(PlannerOptionsValidator.Validate(low));
        Assert.Empty(PlannerOptionsValidator.Validate(high));
    }
}
