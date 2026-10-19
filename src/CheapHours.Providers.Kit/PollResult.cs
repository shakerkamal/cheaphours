using CheapHours.Core.Devices;

namespace CheapHours.Providers.Kit;

/// <summary>
/// The outcome of one poll of one account. Exactly two outcomes exist (the private constructor stops
/// others): vendor failures are reported here, never thrown.
/// </summary>
public abstract record PollResult
{
    private PollResult()
    {
    }

    /// <summary>The poll finished; the events may include readings, status changes and removed devices.</summary>
    public sealed record Completed : PollResult
    {
        /// <summary>Creates a completed result.</summary>
        /// <param name="events">What was observed, possibly nothing.</param>
        public Completed(IReadOnlyList<DeviceEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            Events = [.. events];
        }

        /// <summary>Gets what was observed.</summary>
        public IReadOnlyList<DeviceEvent> Events { get; }
    }

    /// <summary>The vendor could not be polled this time (429, 5xx, timeout, connection failure).</summary>
    public sealed record Failed : PollResult
    {
        /// <summary>Creates a failed result.</summary>
        /// <param name="reason">What went wrong.</param>
        /// <param name="retryAfter">How long the vendor asked us to wait, if it said.</param>
        public Failed(string reason, TimeSpan? retryAfter = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (retryAfter < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Retry-after cannot be negative.");
            }

            Reason = reason;
            RetryAfter = retryAfter;
        }

        /// <summary>Gets what went wrong.</summary>
        public string Reason { get; }

        /// <summary>Gets how long to wait before polling again, if the vendor said.</summary>
        public TimeSpan? RetryAfter { get; }
    }
}
