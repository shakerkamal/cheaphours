using System.Diagnostics.Metrics;
using CheapHours.Host.Observability;

namespace CheapHours.Host.Worker;

/// <summary>
/// Counts the telemetry readings the worker stores, per provider (operational-metrics: "Stored telemetry readings
/// are counted"). The counts come from what the writer stored, so a redelivered message adds nothing.
/// </summary>
internal sealed class TelemetryMetrics
{
    /// <summary>The counter's name.</summary>
    public const string ReadingsName = "cheaphours.telemetry.readings";

    private readonly Counter<long> _readings;

    public TelemetryMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _readings = meters.Create(CheapHoursDiagnostics.TelemetryName)
            .CreateCounter<long>(ReadingsName, unit: "{reading}", description: "Telemetry readings stored, by provider.");
    }

    /// <summary>Counts one stored batch.</summary>
    /// <param name="storedPerProvider">The readings the writer stored, per provider id.</param>
    public void Stored(IReadOnlyDictionary<string, int> storedPerProvider)
    {
        ArgumentNullException.ThrowIfNull(storedPerProvider);
        foreach (var (providerId, count) in storedPerProvider)
        {
            if (count > 0)
            {
                _readings.Add(count, new KeyValuePair<string, object?>("provider.id", providerId));
            }
        }
    }
}
