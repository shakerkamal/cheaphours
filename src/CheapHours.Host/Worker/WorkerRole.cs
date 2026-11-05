using CheapHours.Host.Devices;
using CheapHours.Host.Polling;
using CheapHours.Host.Prices;
using CheapHours.Messaging;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CheapHours.Host.Worker;

/// <summary>Builds the worker process (scheduled jobs, message consumers and vendor-cloud polling).</summary>
internal static class WorkerRole
{
    /// <summary>The name the broker shows for the worker's connection.</summary>
    public const string ClientName = "cheaphours-worker";

    /// <summary>The Data Protection application name; replicas that share a key ring must use the same.</summary>
    public const string DataProtectionApplication = "CheapHours";

    /// <summary>
    /// Creates the worker host: startup (indexes, provider accounts, topology), the telemetry consumer and, for
    /// every configured polling provider, polling by the replica that holds the polling lease.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configure">Extra registrations, for tests.</param>
    /// <returns>The configured, not yet started, host.</returns>
    public static IHost Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.TryAddSingleton(TimeProvider.System);
        services.AddMongoPersistence(configuration);
        services.AddRabbitMqMessaging(configuration, ClientName);

        services.AddSingleton<TelemetryBatchWriter>();
        services.AddSingleton<TelemetryBatchHandler>();
        services.AddSingleton<WorkerState>();

        // Vendor tokens are encrypted with this key ring. Without a configured path the default per-user
        // location is used (local runs); Kubernetes mounts DataProtection:KeysPath.
        var dataProtection = services.AddDataProtection().SetApplicationName(DataProtectionApplication);
        if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        if (configuration.GetSection(CarCloudOptions.SectionName).Exists())
        {
            services.AddProvider<CarCloudProvider, CarCloudOptions>(CarCloudOptions.SectionName);
        }

        services.AddOptions<PollingOptions>()
            .BindConfiguration(PollingOptions.SectionName)
            .Validate(o => o.LeaseDuration > TimeSpan.Zero && o.RenewInterval > TimeSpan.Zero && o.RenewInterval < o.LeaseDuration, "Polling: RenewInterval must be positive and shorter than LeaseDuration.")
            .Validate(o => o.AcquireInterval > TimeSpan.Zero && o.Tick > TimeSpan.Zero, "Polling: AcquireInterval and Tick must be positive.")
            .Validate(o => o.MaxConcurrentPolls >= 1, "Polling: MaxConcurrentPolls must be at least 1.")
            .ValidateOnStart();
        services.AddSingleton<DeviceEventRecorder>();
        services.AddSingleton<DeviceEventIngestor>();
        services.AddSingleton<IPollRecorder, PollResultRecorder>();

        // Day-ahead prices: the job is always registered (tests drive single runs), but runs only when enabled.
        services.AddPriceSource();
        services.AddSingleton<PriceImportJob>();

        services.AddHostedService<WorkerStartup>();
        services.AddHostedService<TelemetryConsumerService>();
        services.AddHostedService<PollingCoordinator>();
        if (configuration.GetValue(PricesOptions.SectionName + ":" + nameof(PricesOptions.ImportEnabled), defaultValue: true))
        {
            services.AddHostedService(sp => sp.GetRequiredService<PriceImportJob>());
        }

        configure?.Invoke(services);
        return builder.Build();
    }
}
