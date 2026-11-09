using System.Reflection;
using CheapHours.Host.Gateway;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CheapHours.Host.Observability;

/// <summary>
/// Traces, metrics and logs for every role (ADR-009): the CheapHours sources and meters, ASP.NET Core and HttpClient
/// instrumentation, the runtime meter, and OTLP export only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// </summary>
internal static class ObservabilitySetup
{
    /// <summary>The configuration key (an environment variable) that switches OTLP export on.</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private const string RuntimeMeter = "System.Runtime";

    /// <summary>Gets the build's version, as exported in <c>service.version</c>.</summary>
    public static string ServiceVersion { get; } =
        typeof(ObservabilitySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ObservabilitySetup).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Registers tracing, metrics and logging for <paramref name="role"/>.</summary>
    /// <param name="builder">The role's application builder.</param>
    /// <param name="role">The role, which names the service.</param>
    /// <param name="instanceId">The replica's id (<c>service.instance.id</c>).</param>
    /// <returns>The builder.</returns>
    public static IHostApplicationBuilder AddCheapHoursObservability(this IHostApplicationBuilder builder, HostRole role, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        // Sampling is left to the SDK default, parent-based always-on, so OTEL_TRACES_SAMPLER can change it later.
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                ServiceName(role),
                serviceVersion: ServiceVersion,
                autoGenerateServiceInstanceId: false,
                serviceInstanceId: instanceId))
            .WithTracing(tracing => tracing
                .AddSource([.. CheapHoursDiagnostics.SourceNames])
                .AddAspNetCoreInstrumentation(options => options.Filter = context => TracesRequest(context.Request.Path))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter([.. CheapHoursDiagnostics.MeterNames])
                .AddMeter(RuntimeMeter)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
            });

        ConfigureConsole(builder);

        if (ExportsTo(builder.Configuration))
        {
            // Reads the endpoint and OTEL_EXPORTER_OTLP_PROTOCOL itself; batch processors with bounded queues, so a
            // dead collector drops telemetry and never blocks a request, a frame or a message.
            telemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Console logs: one JSON object per line outside Development (UTC, scopes), plain text with scopes in
    /// Development. Every entry written inside a span carries its trace and span ids as a scope.
    /// </summary>
    /// <param name="builder">The role's application builder.</param>
    private static void ConfigureConsole(IHostApplicationBuilder builder)
    {
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
            return;
        }

        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'";
        });
    }

    /// <summary>Returns the exported service name of a role.</summary>
    /// <param name="role">The role.</param>
    /// <returns><c>cheaphours-api</c>, <c>cheaphours-gateway</c> or <c>cheaphours-worker</c>.</returns>
    public static string ServiceName(HostRole role) => "cheaphours-" + role.ToString().ToLowerInvariant();

    /// <summary>Returns the replica's instance id: the gateway's replica id, or the machine name for the other roles.</summary>
    /// <param name="role">The role.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The instance id.</returns>
    public static string InstanceId(HostRole role, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (role != HostRole.Gateway)
        {
            return Environment.MachineName;
        }

        return configuration[$"{GatewayOptions.SectionName}:{nameof(GatewayOptions.ReplicaId)}"] is { Length: > 0 } replicaId
            ? replicaId
            : GatewayOptions.DefaultReplicaId(Environment.MachineName);
    }

    /// <summary>Returns whether OTLP export is configured.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns><see langword="true"/> when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> holds a value.</returns>
    public static bool ExportsTo(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);
    }

    /// <summary>
    /// Returns whether an incoming request gets a span. Health probes are noise, and <c>/ocpp</c> lasts as long as the
    /// charger's connection: each frame gets its own trace instead.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="false"/> for <c>/health/*</c> and <c>/ocpp/*</c>.</returns>
    public static bool TracesRequest(PathString path) =>
        !path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/ocpp", StringComparison.OrdinalIgnoreCase);
}
