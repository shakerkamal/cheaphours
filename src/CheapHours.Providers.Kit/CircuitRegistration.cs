namespace CheapHours.Providers.Kit;

/// <summary>A provider registered with an HTTP pipeline, and so with a circuit breaker to report.</summary>
/// <param name="ProviderType">The provider's type.</param>
internal sealed record CircuitRegistration(Type ProviderType);
