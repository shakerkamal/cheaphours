namespace CheapHours.Providers.Kit;

/// <summary>What kind of failure a vendor call ended in.</summary>
public enum VendorFailureKind
{
    /// <summary>Worth retrying later: 429, 5xx, 408, timeouts, connection failures, an open circuit or our own rate limit.</summary>
    Transient,

    /// <summary>A permanent refusal (a 4xx other than 401, 404, 408 and 429); do not retry.</summary>
    Rejected,

    /// <summary>The vendor does not know the device (404).</summary>
    UnknownDevice,

    /// <summary>The account's tokens no longer work; the owner must link the account again.</summary>
    ReconnectNeeded,
}
