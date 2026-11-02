using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.HeatPumpCloud;

/// <summary>Registering the heat-pump-cloud provider and validating its configuration at startup.</summary>
public sealed class HeatPumpCloudOptionsTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        ["Providers:HeatPumpCloud:SigningSecret"] = HeatPumpCloudVerifyTests.Secret,
        ["Providers:HeatPumpCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:HeatPumpCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
    };

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddProvider<HeatPumpCloudProvider, HeatPumpCloudOptions>(HeatPumpCloudOptions.SectionName);
        return builder.Build();
    }

    [Fact]
    public async Task Registered_provider_declares_telemetry_and_pushes_events_only()
    {
        using var host = Build(Valid());

        await host.StartAsync(TestContext.Current.CancellationToken);

        var provider = Assert.Single(host.Services.GetServices<IDeviceProvider>());
        Assert.Equal("heatpumpcloud", provider.ProviderId);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.PushesEvents, provider.Capabilities);
        Assert.IsAssignableFrom<IInboundTranslator>(provider);
        Assert.IsAssignableFrom<IWebhookVerifier>(provider);
        Assert.IsNotAssignableFrom<IPollingProvider>(provider);
        Assert.IsNotAssignableFrom<ICommandSender>(provider);
        Assert.Empty(host.Services.GetServices<ProviderPollingSettings>());
        var directory = host.Services.GetRequiredService<WebhookAccountDirectory>();
        Assert.Equal(OwnerId.Parse("own_demo"), directory.FindOwner("heatpumpcloud", "acct_demo"));
        Assert.Equal(TimeSpan.FromSeconds(60), directory.ReportInterval("heatpumpcloud"));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_missing_secret_fails_startup_naming_it()
    {
        var settings = Valid();
        settings.Remove("Providers:HeatPumpCloud:SigningSecret");
        using var host = Build(settings);

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Providers:HeatPumpCloud:SigningSecret", error.Message, StringComparison.Ordinal);
    }
}
