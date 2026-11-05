using System.Globalization;
using System.Linq.Expressions;
using MongoDB.Driver;

namespace CheapHours.Persistence.Prices;

/// <summary>
/// The <c>priceDays</c> collection: one document per zone and Berlin day. Each mark is set once, by an update
/// filtered on the mark being absent, so a repeat changes nothing.
/// </summary>
/// <param name="database">The database.</param>
public sealed class PriceDayStore(IMongoDatabase database) : IPriceDayStore
{
    private readonly IMongoCollection<PriceDayDocument> _days = Collection(database);

    /// <inheritdoc/>
    public async Task<PriceDayState?> GetAsync(string zone, DateOnly day, CancellationToken cancellationToken)
    {
        var id = IdOf(zone, day);
        var document = await _days.Find(d => d.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToState(document);
    }

    /// <inheritdoc/>
    public async Task<PriceDayState> RecordImportAsync(string zone, DateOnly day, int slotCount, bool complete, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureAsync(zone, day, slotCount, cancellationToken).ConfigureAwait(false);
        if (complete)
        {
            await SetOnceAsync(zone, day, d => d.CompleteAt, now, cancellationToken).ConfigureAwait(false);
        }

        return (await GetAsync(zone, day, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public Task MarkAnnouncedAsync(string zone, DateOnly day, DateTimeOffset now, CancellationToken cancellationToken) =>
        SetOnceAsync(zone, day, d => d.AnnouncedAt, now, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> MarkGaveUpAsync(string zone, DateOnly day, int slotCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureAsync(zone, day, slotCount, cancellationToken).ConfigureAwait(false);
        return await SetOnceAsync(zone, day, d => d.GaveUpAt, now, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkAttemptedAsync(string zone, DateOnly day, int slotCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureAsync(zone, day, slotCount, cancellationToken).ConfigureAwait(false);
        await SetOnceAsync(zone, day, d => d.AttemptedAt, now, cancellationToken).ConfigureAwait(false);
    }

    private static IMongoCollection<PriceDayDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<PriceDayDocument>(MongoIndexes.PriceDays);
    }

    private static string IdOf(string zone, DateOnly day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        return string.Create(CultureInfo.InvariantCulture, $"{zone}|{day:yyyy-MM-dd}");
    }

    private static PriceDayState ToState(PriceDayDocument d) => new(
        d.Zone,
        DateOnly.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        d.SlotCount,
        UtcTime.FromBson(d.CompleteAt),
        UtcTime.FromBson(d.AnnouncedAt),
        UtcTime.FromBson(d.GaveUpAt),
        UtcTime.FromBson(d.AttemptedAt));

    private async Task EnsureAsync(string zone, DateOnly day, int slotCount, CancellationToken cancellationToken)
    {
        var id = IdOf(zone, day);
        await _days.UpdateOneAsync(
            d => d.Id == id,
            Builders<PriceDayDocument>.Update
                .Set(d => d.SlotCount, slotCount)
                .SetOnInsert(d => d.Zone, zone)
                .SetOnInsert(d => d.Day, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .SetOnInsert(d => d.SchemaVersion, 1),
            new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SetOnceAsync(
        string zone,
        DateOnly day,
        Expression<Func<PriceDayDocument, DateTime?>> mark,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var filter = Builders<PriceDayDocument>.Filter;
        var result = await _days.UpdateOneAsync(
            filter.Eq(d => d.Id, IdOf(zone, day)) & filter.Eq(mark, null),
            Builders<PriceDayDocument>.Update.Set(mark, UtcTime.ToBson(now)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }
}
