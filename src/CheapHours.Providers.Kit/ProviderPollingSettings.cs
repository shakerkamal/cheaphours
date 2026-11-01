namespace CheapHours.Providers.Kit;

/// <summary>
/// How a polling provider is scheduled, recorded by <see cref="ProviderServiceCollectionExtensions.AddProvider{TProvider, TOptions}"/>
/// for every provider with <see cref="HttpProviderOptions"/>, so the scheduler needs no change to
/// <see cref="IPollingProvider"/>.
/// </summary>
/// <param name="ProviderType">The registered provider type.</param>
/// <param name="PollInterval">How often each linked account is polled.</param>
/// <param name="TotalTimeout">The provider's total time allowed for one vendor call.</param>
public sealed record ProviderPollingSettings(Type ProviderType, TimeSpan PollInterval, TimeSpan TotalTimeout);
