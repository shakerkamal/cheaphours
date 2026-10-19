namespace CheapHours.Providers.Kit;

/// <summary>One <c>AddProvider</c> call, remembered so startup can validate it.</summary>
/// <param name="ProviderType">The registered implementation type.</param>
/// <param name="ConfigSection">The configuration section it was registered with.</param>
internal sealed record ProviderRegistration(Type ProviderType, string ConfigSection);
