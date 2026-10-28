using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence;

/// <summary>Collection names and the indexes the application needs, created idempotently at startup.</summary>
public static class MongoIndexes
{
    /// <summary>Charger registrations.</summary>
    public const string ChargePoints = "chargePoints";

    /// <summary>Devices.</summary>
    public const string Devices = "devices";

    /// <summary>The connection registry.</summary>
    public const string Connections = "connections";

    /// <summary>Device readings over time (a time-series collection).</summary>
    public const string Telemetry = "telemetry";

    /// <summary>The ids of messages each consumer has handled.</summary>
    public const string Inbox = "inbox";

    /// <summary>How long readings are kept.</summary>
    public static readonly TimeSpan TelemetryRetention = TimeSpan.FromDays(30);

    /// <summary>How long a handled message id is remembered.</summary>
    public static readonly TimeSpan InboxRetention = TimeSpan.FromDays(7);

    private const int NamespaceExists = 48;

    /// <summary>Creates every collection and index that does not exist yet; existing ones are left as they are.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the indexes exist.</returns>
    public static async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var keys = Builders<BsonDocument>.IndexKeys;

        // chargePoints is looked up by _id only; the collection is created so its _id index exists up front.
        await EnsureCollectionAsync(database, ChargePoints, cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(Devices).Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(keys.Ascending("providerId").Ascending("externalId"), new CreateIndexOptions { Name = "providerId_externalId", Unique = true }),
                new CreateIndexModel<BsonDocument>(keys.Ascending("ownerId"), new CreateIndexOptions { Name = "ownerId" }),
            ],
            cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(Connections).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys.Ascending("replicaId"), new CreateIndexOptions { Name = "replicaId" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureTelemetryAsync(database, cancellationToken).ConfigureAwait(false);
        await database.GetCollection<BsonDocument>(Telemetry).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys.Ascending("meta.deviceId").Ascending("ts"), new CreateIndexOptions { Name = "deviceId_ts" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(Inbox).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys.Ascending("handledAt"), new CreateIndexOptions { Name = "handledAt_ttl", ExpireAfter = InboxRetention }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Time-series collections cannot be converted later, so a collection of the wrong kind is a hard stop;
    // only the retention, which is configuration, is corrected in place.
    private static async Task EnsureTelemetryAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var options = new CreateCollectionOptions
        {
            TimeSeriesOptions = new TimeSeriesOptions("ts", "meta", TimeSeriesGranularity.Seconds),
            ExpireAfter = TelemetryRetention,
        };
        await EnsureCollectionAsync(database, Telemetry, cancellationToken, options).ConfigureAwait(false);

        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", Telemetry) },
            cancellationToken).ConfigureAwait(false);
        var info = await cursor.FirstAsync(cancellationToken).ConfigureAwait(false);
        var timeseries = info["options"].AsBsonDocument.GetValue("timeseries", BsonNull.Value);

        if (info["type"] != "timeseries"
            || timeseries is not BsonDocument fields
            || fields.GetValue("timeField", BsonNull.Value) != "ts"
            || fields.GetValue("metaField", BsonNull.Value) != "meta")
        {
            throw new InvalidOperationException(
                $"Collection '{Telemetry}' exists but is not a time-series collection with time field 'ts' and meta field 'meta'. Drop or rename it.");
        }

        var expireAfter = info["options"].AsBsonDocument.GetValue("expireAfterSeconds", BsonNull.Value);
        var wanted = (long)TelemetryRetention.TotalSeconds;
        if (!expireAfter.IsNumeric || expireAfter.ToInt64() != wanted)
        {
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "collMod", Telemetry }, { "expireAfterSeconds", wanted } },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureCollectionAsync(IMongoDatabase database, string name, CancellationToken cancellationToken, CreateCollectionOptions? options = null)
    {
        try
        {
            await database.CreateCollectionAsync(name, options, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceExists)
        {
            // Already there, perhaps created by another replica starting at the same time.
        }
    }
}
