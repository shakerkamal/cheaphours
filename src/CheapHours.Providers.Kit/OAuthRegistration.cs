namespace CheapHours.Providers.Kit;

/// <summary>One OAuth provider registration, remembered so its configured accounts can be seeded.</summary>
/// <param name="ProviderType">The registered provider type.</param>
/// <param name="Options">Resolves the provider's options.</param>
internal sealed record OAuthRegistration(Type ProviderType, Func<IServiceProvider, OAuthProviderOptions> Options);
