using CheapHours.Providers.Kit;

namespace CheapHours.Persistence.ProviderAccounts;

/// <summary>
/// When each linked account is next polled. The schedule lives with the account, so a vendor's pause
/// (its <c>Retry-After</c>) survives a change of polling leader.
/// </summary>
public interface IPollingSchedule
{
    /// <summary>
    /// Lists the provider's accounts that are due: not marked reconnect-needed, next poll absent or not after
    /// <paramref name="now"/>, not paused past <paramref name="now"/>, and not in <paramref name="exclude"/>.
    /// The longest-waiting accounts come first.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="exclude">Vendor account ids to leave out (polls still in flight).</param>
    /// <param name="limit">The most accounts to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The due accounts.</returns>
    Task<IReadOnlyList<ProviderAccount>> ListDueAsync(string providerId, DateTimeOffset now, IReadOnlyCollection<string> exclude, int limit, CancellationToken cancellationToken);

    /// <summary>Stores when the account is next due and, after a vendor's <c>Retry-After</c>, until when it is paused.</summary>
    /// <param name="account">The account.</param>
    /// <param name="nextPollAt">When it is next due (UTC).</param>
    /// <param name="pausedUntil">Until when it is paused (UTC), or <see langword="null"/> to clear a pause.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task RecordPollAsync(ProviderAccount account, DateTimeOffset nextPollAt, DateTimeOffset? pausedUntil, CancellationToken cancellationToken);
}
