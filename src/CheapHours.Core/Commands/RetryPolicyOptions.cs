namespace CheapHours.Core.Commands;

/// <summary>
/// How often and how fast a command is retried, and how long to wait for the device to confirm it. With the
/// defaults, the worst case before dead-lettering is about 2.6 minutes (5 + 10 + 20 + 40 + 80 s), well inside
/// a 15-minute slot.
/// </summary>
public sealed class RetryPolicyOptions
{
    /// <summary>Gets or sets how many send attempts a command gets before it is dead-lettered. Default 6.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>Gets or sets the delay cap after the first failed attempt; it doubles with each attempt. Default 5 s.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the largest delay cap, however many attempts have failed. Default 2 min.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how long a sent command may wait for the device to confirm before it is reconciled. Default 60 s.</summary>
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
