namespace CheapHours.Providers.Kit;

/// <summary>One webhook provider registration, remembered so its configured accounts can be looked up.</summary>
/// <param name="ProviderType">The registered provider type.</param>
/// <param name="Options">Resolves the provider's options.</param>
internal sealed record WebhookRegistration(Type ProviderType, Func<IServiceProvider, WebhookProviderOptions> Options);
