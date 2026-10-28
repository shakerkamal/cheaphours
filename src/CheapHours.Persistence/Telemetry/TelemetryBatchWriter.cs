using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Telemetry;

/// <summary>
/// Stores batches of delivered readings exactly once per message and consumer, without transactions
/// (time-series collections allow none): handled ids are dropped through the inbox, a repeated message
/// first removes what an interrupted earlier attempt wrote, and the inbox entry is written last.
/// </summary>
public sealed class TelemetryBatchWriter
{
    private readonly IMongoCollection<TelemetryDocument> _telemetry;
    private readonly IMongoCollection<InboxDocument> _inbox;
    private readonly IMongoCollection<BsonDocument> _devices;
    private readonly TimeProvider _time;

    /// <summary>Creates the writer.</summary>
    /// <param name="database">The database.</param>
    /// <param name="time">The clock for inbox entries.</param>
    public TelemetryBatchWriter(IMongoDatabase database, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        MongoSetup.RegisterConventions();
        _telemetry = database.GetCollection<TelemetryDocument>(MongoIndexes.Telemetry);
        _inbox = database.GetCollection<InboxDocument>(MongoIndexes.Inbox);
        _devices = database.GetCollection<BsonDocument>(MongoIndexes.Devices);
        _time = time;
    }

    /// <summary>Stores a batch.</summary>
    /// <param name="consumer">The consumer name, which scopes the inbox.</param>
    /// <param name="batch">The delivered messages; duplicates and already handled ones are skipped.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when every message of the batch is stored and recorded as handled.</returns>
    /// <exception cref="MongoException">The database failed; the whole batch may be written again as a repeat.</exception>
    public async Task WriteAsync(string consumer, IReadOnlyList<TelemetryBatchItem> batch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentNullException.ThrowIfNull(batch);

        // A message delivered twice in one batch counts once, and as a repeat if either copy is one.
        var unique = batch
            .GroupBy(i => i.MessageId)
            .Select(g => g.First() with { IsRepeat = g.Any(i => i.IsRepeat) })
            .ToList();
        if (unique.Count == 0)
        {
            return;
        }

        var fresh = await DropHandledAsync(consumer, unique, cancellationToken).ConfigureAwait(false);
        if (fresh.Count == 0)
        {
            return;
        }

        await RemovePartialWritesAsync(fresh.Where(i => i.IsRepeat).ToList(), cancellationToken).ConfigureAwait(false);

        var documents = fresh.SelectMany(i => i.Readings.Select(r => ToDocument(i, r))).ToList();
        if (documents.Count > 0)
        {
            await _telemetry.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        await MoveLastReadingsForwardAsync(fresh, cancellationToken).ConfigureAwait(false);
        await RecordHandledAsync(consumer, fresh, cancellationToken).ConfigureAwait(false);
    }

    // One update per device, with only the batch's newest reading: older ones could never win anyway. A
    // device that does not exist is simply not matched; readings never create a device.
    private async Task MoveLastReadingsForwardAsync(List<TelemetryBatchItem> items, CancellationToken cancellationToken)
    {
        var updates = items
            .SelectMany(i => i.Readings.Select(r => (i.DeviceId, Reading: r)))
            .GroupBy(x => x.DeviceId)
            .Select(g => g.MaxBy(x => x.Reading.MeasurementTime))
            .Select(x => new UpdateOneModel<BsonDocument>(new BsonDocument("_id", x.DeviceId.Value), LastReadingUpdate.For(x.Reading)))
            .ToList();

        if (updates.Count > 0)
        {
            await _devices.BulkWriteAsync(updates, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string InboxKey(string consumer, Guid messageId) => $"{consumer}:{messageId}";

    private static TelemetryDocument ToDocument(TelemetryBatchItem item, Core.Devices.DeviceReading reading) => new()
    {
        Ts = UtcTime.ToBson(reading.MeasurementTime),
        Meta = new TelemetryDocument.MetaDocument { DeviceId = item.DeviceId.Value, ProviderId = item.ProviderId },
        EnergyWh = reading.EnergyWh,
        PowerW = reading.PowerW,
        SocPercent = reading.SocPercent is { } soc ? (double)soc : null,
        MessageId = item.MessageId.ToString(),
    };

    private async Task<List<TelemetryBatchItem>> DropHandledAsync(string consumer, List<TelemetryBatchItem> items, CancellationToken cancellationToken)
    {
        var keys = items.Select(i => InboxKey(consumer, i.MessageId)).ToList();
        var handled = await _inbox
            .Find(Builders<InboxDocument>.Filter.In(d => d.Id, keys))
            .Project(d => d.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var seen = handled.ToHashSet(StringComparer.Ordinal);
        return items.Where(i => !seen.Contains(InboxKey(consumer, i.MessageId))).ToList();
    }

    // Narrowed by device and time range so the delete uses the deviceId_ts index; only repeats pay for it.
    private async Task RemovePartialWritesAsync(List<TelemetryBatchItem> repeats, CancellationToken cancellationToken)
    {
        var filters = repeats
            .Where(i => i.Readings.Count > 0)
            .Select(i =>
            {
                var f = Builders<TelemetryDocument>.Filter;
                return f.Eq(d => d.Meta.DeviceId, i.DeviceId.Value)
                    & f.Gte(d => d.Ts, UtcTime.ToBson(i.Readings.Min(r => r.MeasurementTime)))
                    & f.Lte(d => d.Ts, UtcTime.ToBson(i.Readings.Max(r => r.MeasurementTime)))
                    & f.Eq(d => d.MessageId, i.MessageId.ToString());
            })
            .ToList();

        if (filters.Count > 0)
        {
            await _telemetry.DeleteManyAsync(Builders<TelemetryDocument>.Filter.Or(filters), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordHandledAsync(string consumer, List<TelemetryBatchItem> items, CancellationToken cancellationToken)
    {
        var handledAt = UtcTime.ToBson(_time.GetUtcNow());
        var entries = items.Select(i => new InboxDocument { Id = InboxKey(consumer, i.MessageId), HandledAt = handledAt }).ToList();

        try
        {
            await _inbox.InsertManyAsync(entries, new InsertManyOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<InboxDocument> ex) when (ex.WriteConcernError is null && ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // Another worker recorded the same message meanwhile; it is handled either way.
        }
    }
}
