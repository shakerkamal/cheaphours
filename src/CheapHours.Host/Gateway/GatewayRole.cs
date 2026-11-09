using CheapHours.Host.Devices;
using CheapHours.Host.Health;
using CheapHours.Host.Observability;
using CheapHours.Messaging;
using CheapHours.Persistence;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Builds the device gateway process, the public edge for devices and vendors: OCPP charger WebSockets and signed
/// vendor webhooks (the heat-pump cloud when <c>Providers:HeatPumpCloud</c> exists).
/// </summary>
internal static class GatewayRole
{
    /// <summary>The tag of the checks behind <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>The name the broker shows for the gateway's connection.</summary>
    public const string ClientName = "cheaphours-gateway";

    /// <summary>Creates the gateway application.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configured, not yet started, application.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddCheapHoursObservability(HostRole.Gateway, ObservabilitySetup.InstanceId(HostRole.Gateway, builder.Configuration));
        var services = builder.Services;

        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
        services.TryAddSingleton(TimeProvider.System);

        services.AddMongoPersistence(builder.Configuration);
        services.AddRabbitMqMessaging(builder.Configuration, ClientName);

        services.AddSingleton<OcppSchemaValidator>();
        services.AddSingleton<IValidateOptions<OcppOptions>, OcppOptionsValidator>();
        services.AddProvider<OcppProvider, OcppOptions>(OcppOptions.SectionName);
        if (builder.Configuration.GetSection(HeatPumpCloudOptions.SectionName).Exists())
        {
            services.AddProvider<HeatPumpCloudProvider, HeatPumpCloudOptions>(HeatPumpCloudOptions.SectionName);
        }

        services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
        services.AddOptions<GatewayCommandsOptions>().BindConfiguration(GatewayCommandsOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewayCommandsOptions>, GatewayCommandsOptionsValidator>();

        services.AddSingleton<GatewayState>();
        services.AddSingleton<ConnectionTable>();
        services.AddProviderMetrics();
        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<ChargerAuthenticator>();
        services.AddSingleton<DeviceEventRecorder>();
        services.AddSingleton<DeviceEventIngestor>();
        services.AddSingleton<ChargerSession>();

        // The drain service is registered first so it hooks ApplicationStopping before anything else stops.
        services.AddHostedService<GatewayDrainService>();
        services.AddHostedService<GatewayStartup>();
        services.AddHostedService<HeartbeatMonitor>();
        services.AddSingleton<GatewayCommandConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayCommandConsumer>());

        services.AddHealthChecks()
            .AddCheck<GatewayReadinessHealthCheck>("gateway", tags: [ReadyTag])
            .AddCheck<MongoPingHealthCheck>("mongo", tags: [ReadyTag])
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [ReadyTag]);

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        app.MapOcppEndpoint();
        app.MapWebhookEndpoint();

        return app;
    }
}
