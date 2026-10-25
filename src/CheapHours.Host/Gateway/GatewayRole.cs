using CheapHours.Persistence;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Gateway;

/// <summary>Builds the device gateway process: OCPP charger WebSockets (vendor webhooks come with the cloud providers).</summary>
internal static class GatewayRole
{
    /// <summary>The tag of the checks behind <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Creates the gateway application.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configured, not yet started, application.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var services = builder.Services;

        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
        services.TryAddSingleton(TimeProvider.System);

        services.AddMongoPersistence(builder.Configuration);

        services.AddSingleton<OcppSchemaValidator>();
        services.AddSingleton<IValidateOptions<OcppOptions>, OcppOptionsValidator>();
        services.AddProvider<OcppProvider, OcppOptions>(OcppOptions.SectionName);
        services.AddSingleton(sp => new OcppCallHandler(sp.GetRequiredService<OcppSchemaValidator>(), sp.GetRequiredService<IOptions<OcppOptions>>().Value.HeartbeatInterval));

        services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();

        services.AddSingleton<GatewayState>();
        services.AddSingleton<ConnectionTable>();
        services.AddSingleton<ChargerAuthenticator>();
        services.AddSingleton<ChargerSession>();

        // The drain service is registered first so it hooks ApplicationStopping before anything else stops.
        services.AddHostedService<GatewayDrainService>();
        services.AddHostedService<GatewayStartup>();
        services.AddHostedService<HeartbeatMonitor>();

        services.AddHealthChecks()
            .AddCheck<GatewayReadinessHealthCheck>("gateway", tags: [ReadyTag])
            .AddCheck<MongoPingHealthCheck>("mongo", tags: [ReadyTag]);

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        app.MapOcppEndpoint();

        return app;
    }
}
