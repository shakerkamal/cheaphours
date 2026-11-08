using CheapHours.Core.Commands;

namespace CheapHours.Host.Commands;

/// <summary>Command dispatch settings (configuration section <c>Commands</c>, design §12). None of them is a secret.</summary>
internal sealed class CommandsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Commands";

    /// <summary>Gets or sets how many send attempts a command gets before it is dead-lettered. Default 6.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>Gets or sets the delay cap after the first failed attempt; it doubles with each attempt. Default 5 s.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the largest delay cap. Default 2 min.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how long a sent command waits for the device to confirm before it is reconciled. Default 60 s.</summary>
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets how long a due command may wait unsent before a re-announcement counts against it. Default 2 min.</summary>
    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how many counted re-announcements a command gets before it is dead-lettered. Default 3.</summary>
    public int MaxReannouncements { get; set; } = 3;

    /// <summary>Gets or sets how long a dispatcher holds a command while handing it over; longer than the gateway's call timeout. Default 45 s.</summary>
    public TimeSpan ClaimDuration { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Gets or sets how often the command monitor runs. Default 2 s.</summary>
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets how often the outbox relay runs. Default 500 ms.</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets or sets how many outbox entries the relay publishes per run. Default 100.</summary>
    public int RelayBatchSize { get; set; } = 100;

    /// <summary>Returns the retry numbers for <see cref="RetryPolicy"/> and the reconcile decider.</summary>
    /// <returns>The retry options.</returns>
    public RetryPolicyOptions ToRetryPolicyOptions() => new()
    {
        MaxAttempts = MaxAttempts,
        BaseDelay = BaseDelay,
        MaxDelay = MaxDelay,
        ConfirmationTimeout = ConfirmationTimeout,
    };
}
