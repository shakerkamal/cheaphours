namespace CheapHours.Providers.Kit;

/// <summary>The state of a provider's circuit breaker, as reported by <c>cheaphours.provider.circuit.state</c>.</summary>
public enum ProviderCircuitState
{
    /// <summary>Calls go through.</summary>
    Closed = 0,

    /// <summary>The break has passed and a trial call decides whether the circuit closes again.</summary>
    HalfOpen = 1,

    /// <summary>Calls are refused without reaching the vendor until the break passes.</summary>
    Open = 2,
}
