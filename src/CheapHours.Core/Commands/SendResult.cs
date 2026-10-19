namespace CheapHours.Core.Commands;

/// <summary>
/// The outcome of handing a command to a provider. Exactly three outcomes exist: the private constructor
/// stops any other subtype. Vendor failures are reported here, never thrown.
/// </summary>
public abstract record SendResult
{
    private SendResult()
    {
    }

    /// <summary>The vendor took the command; the device has not confirmed it yet.</summary>
    public sealed record Accepted : SendResult
    {
        /// <summary>Creates an accepted result.</summary>
        /// <param name="vendorReference">The vendor's reference for the request, if it gives one.</param>
        public Accepted(string? vendorReference = null) => VendorReference = vendorReference;

        /// <summary>Gets the vendor's reference for the request, if any.</summary>
        public string? VendorReference { get; }
    }

    /// <summary>The vendor refused the command permanently; do not retry.</summary>
    public sealed record Rejected : SendResult
    {
        /// <summary>Creates a rejected result.</summary>
        /// <param name="reason">Why it was refused, for example <c>capability-not-supported</c>.</param>
        public Rejected(string reason) => Reason = Guard.NotBlank(reason);

        /// <summary>Gets why the command was refused.</summary>
        public string Reason { get; }
    }

    /// <summary>The send failed in a way worth retrying with backoff.</summary>
    public sealed record Transient : SendResult
    {
        /// <summary>Creates a transient result.</summary>
        /// <param name="reason">What went wrong, for example <c>vendor returned 503</c>.</param>
        /// <param name="retryAfter">How long the vendor asked us to wait, if it said.</param>
        public Transient(string reason, TimeSpan? retryAfter = null)
        {
            if (retryAfter < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Retry-after cannot be negative.");
            }

            Reason = Guard.NotBlank(reason);
            RetryAfter = retryAfter;
        }

        /// <summary>Gets what went wrong.</summary>
        public string Reason { get; }

        /// <summary>Gets how long to wait before retrying, if the vendor said.</summary>
        public TimeSpan? RetryAfter { get; }
    }
}
