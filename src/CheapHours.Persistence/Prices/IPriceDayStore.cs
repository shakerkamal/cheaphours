namespace CheapHours.Persistence.Prices;

/// <summary>The import's progress per zone and Berlin day: complete, announced, given up.</summary>
public interface IPriceDayStore
{
    /// <summary>Reads the progress of <paramref name="zone"/> on <paramref name="day"/>.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The progress, or <see langword="null"/> when the day was never imported.</returns>
    Task<PriceDayState?> GetAsync(string zone, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Records an import of the day: its slot count, and, when <paramref name="complete"/>, the completion time
    /// if none is recorded yet.
    /// </summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="slotCount">The day's slot count.</param>
    /// <param name="complete">Whether every slot now has a price.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The progress after the write.</returns>
    Task<PriceDayState> RecordImportAsync(string zone, DateOnly day, int slotCount, bool complete, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records that the day was fetched, if no fetch is recorded yet.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="slotCount">The day's slot count, for a day never imported.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    Task MarkAttemptedAsync(string zone, DateOnly day, int slotCount, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the day announced, if it is not yet.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when written.</returns>
    Task MarkAnnouncedAsync(string zone, DateOnly day, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks that the import gave up on the day, if it has not yet.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="slotCount">The day's slot count, for a day never imported.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when this call set the mark; <see langword="false"/> when it was set already.</returns>
    Task<bool> MarkGaveUpAsync(string zone, DateOnly day, int slotCount, DateTimeOffset now, CancellationToken cancellationToken);
}
