namespace CheapHours.Host.Prices;

/// <summary>Price import settings (configuration section <c>Prices</c>). None of them is a secret.</summary>
internal sealed class PricesOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Prices";

    /// <summary>Gets or sets the bidding zone. Default <c>DE-LU</c>.</summary>
    public string Zone { get; set; } = "DE-LU";

    /// <summary>Gets or sets the price source's base URL. Default <c>https://api.energy-charts.info</c>.</summary>
    public Uri BaseUrl { get; set; } = new("https://api.energy-charts.info");

    /// <summary>Gets or sets whether the worker imports prices. Default <see langword="true"/>.</summary>
    public bool ImportEnabled { get; set; } = true;

    /// <summary>Gets or sets the Berlin time from which tomorrow's prices are fetched. Default 13:00.</summary>
    public TimeOnly FirstAttemptAt { get; set; } = new(13, 0);

    /// <summary>Gets or sets the Berlin time after which tomorrow is given up. Default 20:00.</summary>
    public TimeOnly GiveUpAt { get; set; } = new(20, 0);

    /// <summary>Gets or sets the time between import runs. Default 1 hour; 1 minute to 6 hours.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Gets or sets the pause between the two requests of a run. Default 2 seconds.</summary>
    public TimeSpan PauseBetweenRequests { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets how long one run may hold the import lease. Default 5 minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the timeout of one request attempt. Default 10 seconds.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the timeout of one request with its retries. Default 30 seconds.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how often a failed request is retried within a run. Default 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Gets or sets the first retry's delay; it doubles with jitter. Default 1 second.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
