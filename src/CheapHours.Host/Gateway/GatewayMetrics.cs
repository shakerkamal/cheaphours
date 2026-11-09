using System.Diagnostics.Metrics;
using CheapHours.Host.Observability;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp;

namespace CheapHours.Host.Gateway;

/// <summary>
/// The gateway's metrics (operational-metrics): the charger connections this replica holds, and each OCPP call made
/// for a command, counted and timed on the same instruments as vendor HTTP calls with <c>provider.id</c> =
/// <c>ocpp</c>.
/// </summary>
internal sealed class GatewayMetrics
{
    /// <summary>The connections gauge's name.</summary>
    public const string ConnectionsName = "cheaphours.gateway.connections";

    private readonly ProviderMetrics _calls;

    public GatewayMetrics(IMeterFactory meters, ConnectionTable connections, ProviderMetrics calls)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(connections);
        _calls = calls;
        meters.Create(CheapHoursDiagnostics.GatewayName).CreateObservableUpDownCounter(
            ConnectionsName,
            () => new Measurement<int>(connections.Count, new KeyValuePair<string, object?>("provider.id", OcppProvider.Id)),
            unit: "{connection}",
            description: "Charger connections held by this gateway replica.");
    }

    /// <summary>Records one OCPP call to a charger: <c>success</c> when it answered, <c>transient</c> otherwise.</summary>
    /// <param name="answered">Whether the charger answered (with any status).</param>
    /// <param name="elapsed">How long the call took.</param>
    public void RecordCall(bool answered, TimeSpan elapsed) =>
        _calls.Record(OcppProvider.Id, answered ? null : new VendorFailure(VendorFailureKind.Transient, "no-answer"), elapsed);
}
