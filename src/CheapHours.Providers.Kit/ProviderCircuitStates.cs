using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CheapHours.Core.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.Providers.Kit;

/// <summary>
/// The circuit-breaker state of every provider registered with an HTTP pipeline, set by the breaker's callbacks.
/// Each provider starts closed, so the gauge reports it before its first call. States are kept by provider type
/// and turned into provider ids only when read, so a breaker callback never has to build a provider.
/// </summary>
public sealed class ProviderCircuitStates
{
    private readonly ConcurrentDictionary<Type, ProviderCircuitState> _states = new();
    private readonly ConcurrentDictionary<Type, string> _ids = new();
    private readonly IServiceProvider _services;

    /// <summary>Starts every registered HTTP provider closed.</summary>
    /// <param name="registrations">The providers registered with an HTTP pipeline.</param>
    /// <param name="services">The services, to read each provider's id when the states are reported.</param>
    internal ProviderCircuitStates(IEnumerable<CircuitRegistration> registrations, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _services = services;
        foreach (var registration in registrations)
        {
            _states.TryAdd(registration.ProviderType, ProviderCircuitState.Closed);
        }
    }

    /// <summary>Returns the current state of each provider, tagged with its <c>provider.id</c>.</summary>
    /// <returns>One measurement per provider.</returns>
    public IEnumerable<Measurement<int>> Observe() =>
        _states.Select(state => new Measurement<int>((int)state.Value, new KeyValuePair<string, object?>("provider.id", IdOf(state.Key))));

    /// <summary>Records a state change of a provider's breaker.</summary>
    /// <param name="providerType">The provider's type.</param>
    /// <param name="state">The new state.</param>
    internal void Set(Type providerType, ProviderCircuitState state) => _states[providerType] = state;

    private string IdOf(Type providerType) =>
        _ids.GetOrAdd(providerType, type => ((IDeviceProvider)_services.GetRequiredService(type)).ProviderId);
}
