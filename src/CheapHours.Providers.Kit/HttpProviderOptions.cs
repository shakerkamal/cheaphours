namespace CheapHours.Providers.Kit;

/// <summary>
/// Options for a provider that calls its vendor over HTTP. A provider whose options type derives from this
/// gets, from <see cref="ProviderServiceCollectionExtensions.AddProvider{TProvider, TOptions}"/>, its own
/// named <see cref="HttpClient"/> and resilience pipeline, a per-account rate limit and
/// <see cref="ProviderHttpClient{TProvider}"/>.
/// </summary>
public class HttpProviderOptions
{
    /// <summary>The shortest poll interval allowed.</summary>
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the vendor's base address; required and absolute.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Gets the resilience pipeline settings.</summary>
    public HttpPipelineOptions Http { get; init; } = new();

    /// <summary>Gets the per-account rate limit.</summary>
    public RateLimitOptions RateLimit { get; init; } = new();

    /// <summary>Gets or sets how often each linked account is polled (at least 5 s); used only when the provider polls.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
}
