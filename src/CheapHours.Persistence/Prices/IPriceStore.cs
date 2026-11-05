using CheapHours.Core.Prices;

namespace CheapHours.Persistence.Prices;

/// <summary>Day-ahead prices, one per zone and 15-minute slot.</summary>
public interface IPriceStore
{
    /// <summary>
    /// Stores <paramref name="slots"/>: a new slot is inserted, a changed price is updated, and an identical
    /// price is left as it is, so importing the same day twice modifies nothing.
    /// </summary>
    /// <param name="slots">The slots to store.</param>
    /// <param name="importedAt">The import time (UTC), kept only for new slots.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many slots were inserted and how many changed.</returns>
    Task<PriceWriteResult> UpsertAsync(IReadOnlyList<PriceSlot> slots, DateTimeOffset importedAt, CancellationToken cancellationToken);

    /// <summary>Reads the slots of <paramref name="zone"/> starting in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), oldest first.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="fromUtc">The first start included (UTC).</param>
    /// <param name="toUtc">The first start excluded (UTC).</param>
    /// <param name="afterUtc">When set, only slots starting after this (a page cursor).</param>
    /// <param name="limit">The most slots to return; zero or less for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The slots in time order.</returns>
    Task<IReadOnlyList<PriceSlot>> ReadAsync(
        string zone,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset? afterUtc,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Counts the stored slots of <paramref name="zone"/> starting in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="fromUtc">The first start included (UTC).</param>
    /// <param name="toUtc">The first start excluded (UTC).</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>The number of priced slots.</returns>
    Task<int> CountAsync(string zone, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}
