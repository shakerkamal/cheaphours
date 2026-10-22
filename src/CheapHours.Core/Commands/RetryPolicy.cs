namespace CheapHours.Core.Commands;

/// <summary>
/// Decides when a command is retried after a transient failure (exponential backoff with full jitter, the
/// vendor's <c>Retry-After</c> as a floor) and when a sent command is overdue for confirmation. This is the
/// durable, outer retry loop: the next attempt time is stored on the command and survives restarts.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryPolicyOptions _options;
    private readonly Random _random;

    /// <summary>Creates the policy.</summary>
    /// <param name="options">The retry numbers.</param>
    /// <param name="random">The jitter source; inject a seeded instance in tests.</param>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public RetryPolicy(RetryPolicyOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.BaseDelay, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDelay, options.BaseDelay, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ConfirmationTimeout, TimeSpan.Zero, nameof(options));
        _options = options;
        _random = random;
    }

    /// <summary>Decides what follows a transient failure.</summary>
    /// <param name="attempt">The 1-based number of the attempt that just failed: the command's <see cref="Command.Attempts"/> plus one, before the decision is applied.</param>
    /// <param name="retryAfter">How long the vendor asked us to wait, if it said.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><see cref="RetryDecision.GiveUp"/> if <paramref name="attempt"/> was the last allowed one; otherwise when to try again.</returns>
    public RetryDecision AfterTransientFailure(int attempt, TimeSpan? retryAfter, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        Guard.Utc(now);
        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Retry-after cannot be negative.");
        }

        if (attempt >= _options.MaxAttempts)
        {
            return new RetryDecision.GiveUp();
        }

        // Full jitter: anywhere between zero and the cap. The vendor's Retry-After is a contract, so it is a
        // floor and is not capped; asking too much simply runs out of attempts and dead-letters.
        var delay = TimeSpan.FromTicks((long)(_random.NextDouble() * Cap(attempt).Ticks));
        if (retryAfter > delay)
        {
            delay = retryAfter.Value;
        }

        return new RetryDecision.RetryAt(now + delay);
    }

    /// <summary>Returns whether a sent command has waited at least the confirmation timeout since it was last sent.</summary>
    /// <param name="command">The command.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><see langword="true"/> if the command is <see cref="CommandState.Sent"/> and due for <see cref="Command.TimeOut"/>.</returns>
    public bool IsConfirmationOverdue(Command command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        Guard.Utc(now);
        return command.State == CommandState.Sent
            && command.LastSentAt is { } sentAt
            && now - sentAt >= _options.ConfirmationTimeout;
    }

    // BaseDelay × 2^(attempt − 1), saturating at MaxDelay before the doubling can overflow.
    private TimeSpan Cap(int attempt)
    {
        var cap = _options.BaseDelay;
        for (var i = 1; i < attempt; i++)
        {
            if (cap.Ticks > _options.MaxDelay.Ticks / 2)
            {
                return _options.MaxDelay;
            }

            cap += cap;
        }

        return cap;
    }
}
