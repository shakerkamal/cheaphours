using System.Collections.Concurrent;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Keeps calls under each vendor account's limit (design §3): one token bucket per (provider, account),
/// holding at most <c>RequestsPerMinute</c> tokens and gaining one every <c>60 s / RequestsPerMinute</c>.
/// The bucket is a virtual schedule on the injected clock: each call reserves the next token's time. A call
/// whose token comes within <see cref="MaxWait"/> waits for it; any other fails at once with the time until
/// the next token. The limiter is per process: only the polling leader polls, and a vendor 429 is the backstop.
/// </summary>
internal sealed class AccountRateLimiter(TimeProvider time) : IDisposable
{
    /// <summary>The longest a call waits for a token.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<(string Provider, string Account), Bucket> _buckets = new();

    /// <summary>Takes one token for the account, waiting at most <see cref="MaxWait"/>.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalAccountId">The vendor's account id.</param>
    /// <param name="requestsPerMinute">The account's limit; fixed for the life of the bucket.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="null"/> when a token was taken; otherwise how long until the next token.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    public async ValueTask<TimeSpan?> AcquireAsync(string providerId, string externalAccountId, int requestsPerMinute, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        var bucket = _buckets.GetOrAdd((providerId, externalAccountId), _ => new Bucket(requestsPerMinute, time.GetUtcNow()));

        var decision = bucket.Reserve(time.GetUtcNow());
        if (decision.RetryAfter is { } retryAfter)
        {
            return retryAfter;
        }

        if (decision.Wait > TimeSpan.Zero)
        {
            await Task.Delay(decision.Wait, time, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public void Dispose() => _buckets.Clear();

    /// <summary>One account's bucket as a virtual schedule (generic cell rate algorithm).</summary>
    private sealed class Bucket(int requestsPerMinute, DateTimeOffset start)
    {
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(1) / requestsPerMinute;
        private readonly TimeSpan _burst = TimeSpan.FromMinutes(1) / requestsPerMinute * (requestsPerMinute - 1);
        private readonly Lock _lock = new();
        private DateTimeOffset _nextFree = start;

        /// <summary>Reserves the next token or reports how long until one is free.</summary>
        /// <param name="now">The current time.</param>
        /// <returns>How long to wait for the reserved token, or the retry-after when none was reserved.</returns>
        public (TimeSpan Wait, TimeSpan? RetryAfter) Reserve(DateTimeOffset now)
        {
            lock (_lock)
            {
                var due = _nextFree > now ? _nextFree : now;
                var wait = due - _burst - now;
                if (wait > MaxWait)
                {
                    return (TimeSpan.Zero, wait);
                }

                _nextFree = due + _interval;
                return (wait > TimeSpan.Zero ? wait : TimeSpan.Zero, null);
            }
        }
    }
}
