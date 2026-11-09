using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Counts and times vendor calls on the <c>CheapHours.Providers</c> meter, tagged with the provider id and the
/// classified outcome only: never an account, a device or a token. Public so that the OCPP gateway records its
/// command calls on the same instruments (<c>provider.id</c> = <c>ocpp</c>).
/// </summary>
public sealed class ProviderMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "CheapHours.Providers";

    /// <summary>The call counter's name.</summary>
    public const string RequestsName = "cheaphours.provider.requests";

    /// <summary>The call duration histogram's name (seconds).</summary>
    public const string DurationName = "cheaphours.provider.request.duration";

    /// <summary>The circuit-breaker state gauge's name (0 closed, 1 half-open, 2 open).</summary>
    public const string CircuitStateName = "cheaphours.provider.circuit.state";

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the instruments on the process's <c>CheapHours.Providers</c> meter.</summary>
    /// <param name="meters">The meter factory.</param>
    /// <param name="circuits">The breaker state of every HTTP provider, reported by the gauge.</param>
    public ProviderMetrics(IMeterFactory meters, ProviderCircuitStates circuits)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(circuits);
        var meter = meters.Create(MeterName);
        _requests = meter.CreateCounter<long>(RequestsName, unit: "{request}", description: "Vendor calls by provider and outcome.");
        _duration = meter.CreateHistogram<double>(DurationName, unit: "s", description: "Duration of vendor calls, including retries.");
        meter.CreateObservableGauge(CircuitStateName, circuits.Observe, unit: "{state}", description: "Circuit breaker of each HTTP provider: 0 closed, 1 half-open, 2 open.");
    }

    /// <summary>The outcome tag for a call.</summary>
    /// <param name="failure">The call's failure, or <see langword="null"/> when it succeeded.</param>
    /// <returns><c>success</c>, <c>transient</c>, <c>rejected</c>, <c>unknown-device</c> or <c>reconnect-needed</c>.</returns>
    public static string Outcome(VendorFailure? failure) => failure?.Kind switch
    {
        null => "success",
        VendorFailureKind.Transient => "transient",
        VendorFailureKind.Rejected => "rejected",
        VendorFailureKind.UnknownDevice => "unknown-device",
        VendorFailureKind.ReconnectNeeded => "reconnect-needed",
        _ => throw new UnreachableException(),
    };

    /// <summary>Records one call.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="failure">The call's failure, or <see langword="null"/> when it succeeded.</param>
    /// <param name="elapsed">How long the call took.</param>
    public void Record(string providerId, VendorFailure? failure, TimeSpan elapsed)
    {
        var tags = new TagList { { "provider.id", providerId }, { "outcome", Outcome(failure) } };
        _requests.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }
}
