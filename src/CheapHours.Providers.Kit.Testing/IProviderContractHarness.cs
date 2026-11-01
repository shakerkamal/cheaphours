using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.Providers.Kit.Testing;

/// <summary>
/// What a provider supplies to run the shared contract suite: its registration against a fake of its vendor,
/// ways to arrange vendor behaviour, probes of what the vendor received, and translator samples. Members that
/// do not apply to the provider's interaction style may throw <see cref="NotSupportedException"/>: the suite
/// skips those rules before calling them.
/// </summary>
public interface IProviderContractHarness : IAsyncDisposable
{
    /// <summary>Gets the configuration the provider is bound to (test options pointing at the fake).</summary>
    IReadOnlyDictionary<string, string?> Configuration { get; }

    /// <summary>Gets whether the provider calls its vendor over HTTP (so the HTTP rules apply).</summary>
    bool UsesHttp { get; }

    /// <summary>Gets whether the vendor uses OAuth 2.0 (so the token rules apply).</summary>
    bool UsesOAuth { get; }

    /// <summary>Gets the linked account to poll or act through, or <see langword="null"/> for providers that need none.</summary>
    ProviderAccount? Account { get; }

    /// <summary>Gets how many vendor requests (not token requests) the fake received.</summary>
    int VendorRequestCount { get; }

    /// <summary>Gets how many token requests the fake received.</summary>
    int TokenRequestCount { get; }

    /// <summary>Gets a known input with its expected reading, for translators.</summary>
    (InboundMessage Input, DeviceReading Expected)? KnownInput { get; }

    /// <summary>Gets an input of a kind the translator does not handle.</summary>
    InboundMessage? UnhandledInput { get; }

    /// <summary>Gets a malformed input.</summary>
    InboundMessage? MalformedInput { get; }

    /// <summary>Registers the provider (through <c>AddProvider</c>) and whatever else it needs.</summary>
    /// <param name="services">The services; configuration, logging, the account store, data protection and a clock are already there.</param>
    void Register(IServiceCollection services);

    /// <summary>Makes the vendor report one device with a reading.</summary>
    /// <returns>The device's vendor id and the reading the provider must report for it, in W, Wh, percent and UTC.</returns>
    Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync();

    /// <summary>Makes the vendor list a device whose details then answer 404.</summary>
    /// <returns>The removed device's vendor id.</returns>
    Task<string> ArrangeRemovedDeviceAsync();

    /// <summary>Makes every vendor request (not token requests) answer <paramref name="status"/>.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="retryAfter">A <c>Retry-After</c> to send, if any.</param>
    /// <param name="body">A JSON body to send, if any.</param>
    /// <returns>A task that completes when arranged.</returns>
    Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null);

    /// <summary>Makes every vendor request wait <paramref name="delay"/> before answering.</summary>
    /// <param name="delay">The delay.</param>
    /// <returns>A task that completes when arranged.</returns>
    Task ArrangeDelayAsync(TimeSpan delay);

    /// <summary>Makes the vendor refuse the access tokens issued so far (401); newly issued ones work.</summary>
    /// <returns>A task that completes when arranged.</returns>
    Task ArrangeAccessTokenRejectedAsync();

    /// <summary>Revokes every refresh and access token: refreshes answer <c>invalid_grant</c>, calls answer 401.</summary>
    /// <returns>A task that completes when arranged.</returns>
    Task ArrangeRefreshRevokedAsync();

    /// <summary>Returns the idempotency keys of the command requests the vendor received for a device.</summary>
    /// <param name="externalId">The device's vendor id.</param>
    /// <returns>The keys, one per request.</returns>
    IReadOnlyList<string> CommandKeysSentFor(string externalId);
}
