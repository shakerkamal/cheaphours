namespace CheapHours.Persistence.Outbox;

/// <summary>
/// The relay's view of the <c>outbox</c>. Entries are added only inside the transaction of the change they
/// announce (by the command and goal stores), never on their own.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Reads unpublished entries whose time has come, oldest first.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="limit">The most entries to return; greater than zero.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="limit"/> entries.</returns>
    Task<IReadOnlyList<OutboxEntry>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Marks an entry published, after the broker confirmed it.</summary>
    /// <param name="messageId">The entry.</param>
    /// <param name="at">When (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the entry was still unpublished.</returns>
    Task<bool> MarkPublishedAsync(Guid messageId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Counts a failed publish of an entry, which stays unpublished.</summary>
    /// <param name="messageId">The entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task RecordFailedAttemptAsync(Guid messageId, CancellationToken cancellationToken);
}
