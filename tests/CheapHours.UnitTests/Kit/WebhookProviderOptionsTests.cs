using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Kit;

/// <summary>Webhook provider options: startup validation and the account lookup.</summary>
public sealed class WebhookProviderOptionsTests
{
    private const string Section = "Providers:Hooks";
    private const string Secret = "hooks-signing-secret-0123456789abcdef";

    private static Dictionary<string, string?> Valid() => new()
    {
        [$"{Section}:SigningSecret"] = Secret,
        [$"{Section}:Accounts:0:Owner"] = "own_demo",
        [$"{Section}:Accounts:0:ExternalAccountId"] = "acct_demo",
        [$"{Section}:Accounts:1:Owner"] = "own_other",
        [$"{Section}:Accounts:1:ExternalAccountId"] = "acct_other",
    };

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddProvider<HooksProvider, HooksOptions>(Section);
        return builder.Build();
    }

    private static async Task<OptionsValidationException> StartFailsAsync(Dictionary<string, string?> settings)
    {
        using var host = Build(settings);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        return error;
    }

    [Fact]
    public async Task Defaults_apply_and_the_options_bind()
    {
        using var host = Build(Valid());

        await host.StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<IOptions<HooksOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(5), options.SignatureTolerance);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ExpectedReportInterval);
        Assert.Equal(2, options.Accounts.Count);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_lookup_finds_a_configured_account_and_nothing_else()
    {
        using var host = Build(Valid());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var directory = host.Services.GetRequiredService<WebhookAccountDirectory>();

        Assert.Equal(OwnerId.Parse("own_demo"), directory.FindOwner("hooks", "acct_demo"));
        Assert.Equal(OwnerId.Parse("own_other"), directory.FindOwner("hooks", "acct_other"));
        Assert.Null(directory.FindOwner("hooks", "acct_unknown"));
        Assert.Null(directory.FindOwner("hooks", "ACCT_DEMO"));
        Assert.Null(directory.FindOwner("othercloud", "acct_demo"));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_lookup_gives_the_report_interval_of_a_webhook_provider_only()
    {
        var settings = Valid();
        settings[$"{Section}:ExpectedReportInterval"] = "00:00:15";
        using var host = Build(settings);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var directory = host.Services.GetRequiredService<WebhookAccountDirectory>();

        Assert.Equal(TimeSpan.FromSeconds(15), directory.ReportInterval("hooks"));
        Assert.Null(directory.ReportInterval("othercloud"));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_missing_secret_fails_startup_naming_the_key()
    {
        var settings = Valid();
        settings.Remove($"{Section}:SigningSecret");

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:SigningSecret is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_secret_fails_startup_without_echoing_it()
    {
        const string shortSecret = "too-short-secret";
        var settings = Valid();
        settings[$"{Section}:SigningSecret"] = shortSecret;

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:SigningSecret", error.Message, StringComparison.Ordinal);
        Assert.Contains("at least 32 characters", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(shortSecret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:00.5")]
    [InlineData("00:15:01")]
    public async Task A_tolerance_outside_one_second_to_fifteen_minutes_fails_startup(string tolerance)
    {
        var settings = Valid();
        settings[$"{Section}:SignatureTolerance"] = tolerance;

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:SignatureTolerance", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData("00:15:00")]
    public async Task The_tolerance_bounds_are_allowed(string tolerance)
    {
        var settings = Valid();
        settings[$"{Section}:SignatureTolerance"] = tolerance;
        using var host = Build(settings);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_report_interval_below_five_seconds_fails_startup()
    {
        var settings = Valid();
        settings[$"{Section}:ExpectedReportInterval"] = "00:00:04";

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:ExpectedReportInterval", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_without_a_valid_owner_or_id_fails_startup_naming_the_fields()
    {
        var settings = Valid();
        settings[$"{Section}:Accounts:0:Owner"] = "demo";
        settings.Remove($"{Section}:Accounts:1:ExternalAccountId");

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:Accounts:0:Owner", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{Section}:Accounts:1:ExternalAccountId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_account_id_fails_startup()
    {
        var settings = Valid();
        settings[$"{Section}:Accounts:1:ExternalAccountId"] = "acct_demo";

        var error = await StartFailsAsync(settings);

        Assert.Contains($"{Section}:Accounts:1:ExternalAccountId repeats another account", error.Message, StringComparison.Ordinal);
    }

    public sealed class HooksOptions : WebhookProviderOptions;

    private sealed class HooksProvider : IInboundTranslator, IWebhookVerifier
    {
        public string ProviderId => "hooks";

        public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.PushesEvents;

        public IReadOnlyList<DeviceEvent> Translate(InboundMessage message) => [];

        public bool Verify(InboundMessage message) => false;
    }
}
