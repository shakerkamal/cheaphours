using CheapHours.Messaging;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CheapHours.Host.Worker;

/// <summary>Builds the worker process (scheduled jobs and message consumers).</summary>
internal static class WorkerRole
{
    /// <summary>The name the broker shows for the worker's connection.</summary>
    public const string ClientName = "cheaphours-worker";

    /// <summary>Creates the worker host: startup (indexes and topology) and the telemetry consumer.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configure">Extra registrations, for tests.</param>
    /// <returns>The configured, not yet started, host.</returns>
    public static IHost Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var services = builder.Services;

        services.TryAddSingleton(TimeProvider.System);
        services.AddMongoPersistence(builder.Configuration);
        services.AddRabbitMqMessaging(builder.Configuration, ClientName);

        services.AddSingleton<TelemetryBatchWriter>();
        services.AddSingleton<TelemetryBatchHandler>();
        services.AddSingleton<WorkerState>();

        services.AddHostedService<WorkerStartup>();
        services.AddHostedService<TelemetryConsumerService>();

        configure?.Invoke(services);
        return builder.Build();
    }
}
