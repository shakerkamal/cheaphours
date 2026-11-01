namespace CheapHours.Host.Polling;

/// <summary>Worker polling settings, section <c>Polling</c> (design §5).</summary>
internal sealed class PollingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Polling";

    /// <summary>Gets or sets how long the polling lease lasts unless renewed.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how often the leader renews the lease; well under <see cref="LeaseDuration"/>.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how often a standby replica tries to acquire the lease.</summary>
    public TimeSpan AcquireInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how often each provider's loop looks for due accounts.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets how many of one provider's polls may run at once.</summary>
    public int MaxConcurrentPolls { get; set; } = 4;
}
