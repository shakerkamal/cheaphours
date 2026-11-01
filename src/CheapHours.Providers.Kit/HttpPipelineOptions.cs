namespace CheapHours.Providers.Kit;

/// <summary>
/// The provider's resilience pipeline, outermost first: total timeout, jittered retry (5xx, 408, attempt
/// timeouts, connection failures; never 429 or other 4xx), circuit breaker, attempt timeout.
/// </summary>
public sealed class HttpPipelineOptions
{
    /// <summary>Gets or sets the time allowed for one call including its retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the time allowed for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets or sets how many times a transient failure is retried (0 disables retries).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Gets or sets the first retry's base delay; later retries back off exponentially, with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets or sets how long the breaker stays open (at least 0.5 s).</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the share of failed attempts in the sampling window that opens the breaker (above 0, at most 1).</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Gets or sets how many attempts the window needs before the breaker can open (at least 2).</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Gets or sets the breaker's sampling window (at least 0.5 s).</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
}
