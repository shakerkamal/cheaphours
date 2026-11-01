namespace CheapHours.Providers.Kit;

/// <summary>The vendor's limit for one account, kept with a token bucket per account.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Gets or sets how many calls one account may make per minute (at least 1).</summary>
    public int RequestsPerMinute { get; set; } = 30;
}
