using System.Globalization;
using CheapHours.Core.Prices;
using MongoDB.Driver;

namespace CheapHours.Persistence.Prices;

/// <summary>
/// The <c>prices</c> collection. Each slot is one document keyed by zone and start; writes are upserts that set
/// the price and keep the first import time, so an identical re-import reports nothing modified.
/// </summary>
/// <param name="database">The database.</param>
public sealed class PriceStore(IMongoDatabase database) : IPriceStore
{
    private readonly IMongoCollection<PriceDocument> _prices = Collection(database);

    /// <inheritdoc/>
    public async Task<PriceWriteResult> UpsertAsync(IReadOnlyList<PriceSlot> slots, DateTimeOffset importedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            return default;
        }

        var imported = UtcTime.ToBson(importedAt);
        var update = Builders<PriceDocument>.Update;
        var writes = slots.Select(s => new UpdateOneModel<PriceDocument>(
            Builders<PriceDocument>.Filter.Eq(d => d.Id, IdOf(s.Zone, s.StartUtc)),
            update.Set(d => d.PriceEurPerMwh, s.PriceEurPerMwh)
                .SetOnInsert(d => d.Zone, s.Zone)
                .SetOnInsert(d => d.Start, UtcTime.ToBson(s.StartUtc))
                .SetOnInsert(d => d.ImportedAt, imported)
                .SetOnInsert(d => d.SchemaVersion, 1))
        {
            IsUpsert = true,
        });

        var result = await _prices.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        return new PriceWriteResult(result.Upserts.Count, (int)result.ModifiedCount);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PriceSlot>> ReadAsync(
        string zone,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset? afterUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        // BSON dates keep milliseconds only, so the cursor is a strict "greater than", never "from + 1 tick".
        var filter = Range(zone, fromUtc, toUtc);
        if (afterUtc is { } after)
        {
            filter &= Builders<PriceDocument>.Filter.Gt(d => d.Start, UtcTime.ToBson(after));
        }

        IFindFluent<PriceDocument, PriceDocument> find = _prices.Find(filter).SortBy(d => d.Start);
        if (limit > 0)
        {
            find = find.Limit(limit);
        }

        var documents = await find.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(d => new PriceSlot(d.Zone, UtcTime.FromBson(d.Start), d.PriceEurPerMwh))];
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string zone, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
        (int)await _prices.CountDocumentsAsync(Range(zone, fromUtc, toUtc), cancellationToken: cancellationToken).ConfigureAwait(false);

    internal static string IdOf(string zone, DateTimeOffset start) =>
        string.Create(CultureInfo.InvariantCulture, $"{zone}|{start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");

    private static IMongoCollection<PriceDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<PriceDocument>(MongoIndexes.Prices);
    }

    private static FilterDefinition<PriceDocument> Range(string zone, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        var filter = Builders<PriceDocument>.Filter;
        return filter.Eq(d => d.Zone, zone)
            & filter.Gte(d => d.Start, UtcTime.ToBson(fromUtc))
            & filter.Lt(d => d.Start, UtcTime.ToBson(toUtc));
    }
}
