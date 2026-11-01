using CheapHours.Core.Devices;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Kit;

/// <summary>Registering the car-cloud provider and validating its configuration at startup.</summary>
public sealed class CarCloudOptionsTests
{
    private const string Secret = "client-secret-91ab";
    private const string SeedToken = "seed-refresh-3c7d";

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Providers:CarCloud:BaseAddress"] = "http://localhost:5090/",
        ["Providers:CarCloud:TokenEndpoint"] = "http://localhost:5090/oauth/token",
        ["Providers:CarCloud:ClientId"] = "cheaphours",
        ["Providers:CarCloud:ClientSecret"] = Secret,
        ["Providers:CarCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
        ["Providers:CarCloud:Accounts:0:RefreshToken"] = SeedToken,
    };

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton<IProviderAccountRepository, InMemoryProviderAccountRepository>();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddProvider<CarCloudProvider, CarCloudOptions>(CarCloudOptions.SectionName);
        return builder.Build();
    }

    [Fact]
    public async Task Registered_provider_declares_telemetry_state_of_charge_and_start_stop_only()
    {
        using var host = Build(Valid());

        await host.StartAsync(TestContext.Current.CancellationToken);

        var provider = Assert.Single(host.Services.GetServices<IDeviceProvider>());
        Assert.Equal("carcloud", provider.ProviderId);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.ReadStateOfCharge | Capabilities.StartStop, provider.Capabilities);
        Assert.IsAssignableFrom<IPollingProvider>(provider);
        Assert.IsAssignableFrom<ICommandSender>(provider);
        Assert.Equal(TimeSpan.FromSeconds(60), host.Services.GetServices<ProviderPollingSettings>().Single().PollInterval);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_client_secret_fails_startup_naming_it()
    {
        var settings = Valid();
        settings.Remove("Providers:CarCloud:ClientSecret");
        using var host = Build(settings);

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ClientSecret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SeedToken, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_interval_below_five_seconds_fails_startup_naming_it()
    {
        var settings = Valid();
        settings["Providers:CarCloud:PollInterval"] = "00:00:02";
        using var host = Build(settings);

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("PollInterval", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SeedToken, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_without_a_refresh_token_fails_startup_naming_the_field()
    {
        var settings = Valid();
        settings.Remove("Providers:CarCloud:Accounts:0:RefreshToken");
        settings["Providers:CarCloud:TokenEndpoint"] = "relative/not-a-uri";
        using var host = Build(settings);

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Accounts:0:RefreshToken", error.Message, StringComparison.Ordinal);
        Assert.Contains("TokenEndpoint", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
    }
}
