using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>A car-cloud provider wired to a fresh vendor fake, with captured logs, for the provider's own tests.</summary>
internal sealed class CarCloudRun : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private CarCloudRun(CarCloudHarness harness, ServiceProvider services, CapturingLoggerProvider logs)
    {
        Harness = harness;
        _services = services;
        Logs = logs;
        Provider = services.GetRequiredService<CarCloudProvider>();
    }

    public CarCloudHarness Harness { get; }

    public CarCloudVendorFake Vendor => Harness.Vendor;

    public CarCloudProvider Provider { get; }

    public CapturingLoggerProvider Logs { get; }

    public ProviderAccount Account => Harness.Account!;

    public static async Task<CarCloudRun> StartAsync()
    {
        var harness = new CarCloudHarness();
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(harness.Configuration).Build());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IProviderAccountRepository>(new InMemoryProviderAccountRepository());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        harness.Register(services);
        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<ProviderAccountSeeder>().SeedAsync(TestContext.Current.CancellationToken);
        return new CarCloudRun(harness, sp, logs);
    }

    public Task<PollResult> PollAsync() => Provider.PollAsync(Account, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Harness.DisposeAsync();
    }
}
