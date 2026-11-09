using System.Diagnostics;
using CheapHours.Messaging;
using CheapHours.Providers.Kit;

namespace CheapHours.Host.Observability;

/// <summary>
/// The Host's span sources and the names of every CheapHours source and meter, so the setup and the tests use one
/// list. Instruments are created per process through <c>IMeterFactory</c> by the classes that record them.
/// </summary>
internal static class CheapHoursDiagnostics
{
    /// <summary>Command dispatch, results and the monitor's actions (source and meter).</summary>
    public const string CommandsName = "CheapHours.Commands";

    /// <summary>OCPP frames and calls to chargers (source and meter).</summary>
    public const string GatewayName = "CheapHours.Gateway";

    /// <summary>Scheduled work: polls and price imports (source).</summary>
    public const string JobsName = "CheapHours.Jobs";

    /// <summary>Storing telemetry batches (source and meter).</summary>
    public const string TelemetryName = "CheapHours.Telemetry";

    /// <summary>The command spans.</summary>
    public static readonly ActivitySource Commands = new(CommandsName);

    /// <summary>The gateway's frame and call spans.</summary>
    public static readonly ActivitySource Gateway = new(GatewayName);

    /// <summary>The scheduled jobs' spans.</summary>
    public static readonly ActivitySource Jobs = new(JobsName);

    /// <summary>The telemetry storing spans.</summary>
    public static readonly ActivitySource Telemetry = new(TelemetryName);

    /// <summary>Gets every CheapHours activity source name.</summary>
    public static IReadOnlyList<string> SourceNames { get; } = [MessagingTracing.SourceName, CommandsName, GatewayName, JobsName, TelemetryName];

    /// <summary>Gets every CheapHours meter name.</summary>
    public static IReadOnlyList<string> MeterNames { get; } = [ProviderMetrics.MeterName, CommandsName, GatewayName, TelemetryName];
}
