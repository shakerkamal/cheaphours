using CheapHours.Core.Commands;

namespace CheapHours.Providers.Kit;

/// <summary>
/// How a vendor call failed, classified the same way for every provider. The reason never contains the
/// vendor's body, tokens or secrets: only fixed texts, status codes and a sanitised vendor error code.
/// </summary>
public sealed record VendorFailure
{
    /// <summary>Our own per-account limit had no token left; the call did not reach the vendor.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The vendor answered 429.</summary>
    public const string VendorRateLimited = "vendor-rate-limited";

    /// <summary>The vendor did not answer in time.</summary>
    public const string Timeout = "vendor-timeout";

    /// <summary>The vendor could not be reached.</summary>
    public const string Unreachable = "vendor-unreachable";

    /// <summary>The provider's circuit breaker is open; the call did not reach the vendor.</summary>
    public const string CircuitOpen = "circuit-open";

    /// <summary>The account's tokens no longer work.</summary>
    public const string ReconnectNeededReason = "reconnect-needed";

    /// <summary>The vendor does not know the device.</summary>
    public const string UnknownDeviceReason = "unknown-device";

    /// <summary>Creates a failure.</summary>
    /// <param name="kind">The kind of failure.</param>
    /// <param name="reason">A fixed, secret-free description.</param>
    /// <param name="retryAfter">How long to wait before trying again, if known.</param>
    public VendorFailure(VendorFailureKind kind, string reason, TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Retry-after cannot be negative.");
        }

        Kind = kind;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the kind of failure.</summary>
    public VendorFailureKind Kind { get; }

    /// <summary>Gets the secret-free description.</summary>
    public string Reason { get; }

    /// <summary>Gets how long to wait before trying again, if known.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Gets whether the failure concerns the whole account rather than one device: a rate limit (ours or the
    /// vendor's), an open circuit or tokens that no longer work. Further calls for the account would fail too.
    /// </summary>
    public bool AffectsAccount =>
        Kind == VendorFailureKind.ReconnectNeeded || Reason is RateLimited or VendorRateLimited or CircuitOpen;

    /// <summary>Returns the command result for this failure.</summary>
    /// <returns>
    /// <see cref="SendResult.Transient"/> for transient failures; otherwise <see cref="SendResult.Rejected"/>,
    /// with <c>unknown-device</c> for an unknown device.
    /// </returns>
    public SendResult ToSendResult() => Kind switch
    {
        VendorFailureKind.Transient => new SendResult.Transient(Reason, RetryAfter),
        VendorFailureKind.UnknownDevice => new SendResult.Rejected(UnknownDeviceReason),
        _ => new SendResult.Rejected(Reason),
    };

    /// <summary>Returns the failed poll result for this failure.</summary>
    /// <returns>A <see cref="PollResult.Failed"/> carrying the reason and retry-after.</returns>
    public PollResult.Failed ToPollFailed() => new(Reason, RetryAfter);
}
