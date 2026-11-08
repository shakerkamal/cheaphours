using CheapHours.Host.Commands;

namespace CheapHours.UnitTests.Host;

public sealed class CommandsOptionsTests
{
    [Fact]
    public void Defaults_follow_the_design_and_are_valid()
    {
        var options = new CommandsOptions();

        Assert.True(new CommandsOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(
            (6, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)),
            (options.MaxAttempts, options.BaseDelay, options.MaxDelay, options.ConfirmationTimeout));
        Assert.Equal(
            (TimeSpan.FromMinutes(2), 3, TimeSpan.FromSeconds(45)),
            (options.DispatchTimeout, options.MaxReannouncements, options.ClaimDuration));
        Assert.Equal(
            (TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), 100),
            (options.MonitorInterval, options.RelayInterval, options.RelayBatchSize));
    }

    [Fact]
    public void Retry_numbers_are_passed_to_the_retry_policy()
    {
        var options = new CommandsOptions { MaxAttempts = 4, BaseDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(40), ConfirmationTimeout = TimeSpan.FromSeconds(90) };

        var retry = options.ToRetryPolicyOptions();

        Assert.Equal((4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(90)), (retry.MaxAttempts, retry.BaseDelay, retry.MaxDelay, retry.ConfirmationTimeout));
    }

    [Theory]
    [InlineData("MaxAttempts", "zero attempts")]
    [InlineData("MaxAttempts", "too many attempts")]
    [InlineData("BaseDelay", "no base delay")]
    [InlineData("BaseDelay", "cap below base")]
    [InlineData("ConfirmationTimeout", "no confirmation timeout")]
    [InlineData("DispatchTimeout", "negative dispatch timeout")]
    [InlineData("MaxReannouncements", "no re-announcements")]
    [InlineData("ClaimDuration", "no claim")]
    [InlineData("MonitorInterval", "no monitor interval")]
    [InlineData("RelayInterval", "no relay interval")]
    [InlineData("RelayBatchSize", "empty batch")]
    [InlineData("RelayBatchSize", "huge batch")]
    public void A_broken_value_fails_naming_its_key_and_not_its_value(string key, string broken)
    {
        var options = new CommandsOptions();
        Break(options, broken);

        var result = new CommandsOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(key, failure, StringComparison.Ordinal);
        Assert.StartsWith("Commands: ", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("1001", failure, StringComparison.Ordinal);
    }

    private static void Break(CommandsOptions o, string broken)
    {
        switch (broken)
        {
            case "zero attempts": o.MaxAttempts = 0; break;
            case "too many attempts": o.MaxAttempts = 21; break;
            case "no base delay": o.BaseDelay = TimeSpan.Zero; break;
            case "cap below base": o.MaxDelay = TimeSpan.FromSeconds(1); break;
            case "no confirmation timeout": o.ConfirmationTimeout = TimeSpan.Zero; break;
            case "negative dispatch timeout": o.DispatchTimeout = TimeSpan.FromSeconds(-1); break;
            case "no re-announcements": o.MaxReannouncements = 0; break;
            case "no claim": o.ClaimDuration = TimeSpan.Zero; break;
            case "no monitor interval": o.MonitorInterval = TimeSpan.Zero; break;
            case "no relay interval": o.RelayInterval = TimeSpan.Zero; break;
            case "empty batch": o.RelayBatchSize = 0; break;
            case "huge batch": o.RelayBatchSize = 1001; break;
            default: throw new ArgumentOutOfRangeException(nameof(broken), broken, null);
        }
    }
}
