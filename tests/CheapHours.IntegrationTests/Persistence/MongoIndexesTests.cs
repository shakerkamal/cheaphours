using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class MongoIndexesTests(MongoFixture mongo)
{
    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(MongoIndexesTests)));

    private async Task<List<BsonDocument>> IndexesAsync(string collection) =>
        await (await _database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Creates_the_device_and_connection_indexes()
    {
        await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

        var devices = await IndexesAsync(MongoIndexes.Devices);
        var unique = Assert.Single(devices, i => i["key"].AsBsonDocument == new BsonDocument { { "providerId", 1 }, { "externalId", 1 } });
        Assert.True(unique.GetValue("unique", false).ToBoolean());
        Assert.Single(devices, i => i["name"] == "ownerId_id" && i["key"].AsBsonDocument == new BsonDocument { { "ownerId", 1 }, { "_id", 1 } });

        var connections = await IndexesAsync(MongoIndexes.Connections);
        Assert.Single(connections, i => i["key"].AsBsonDocument == new BsonDocument("replicaId", 1));

        var chargePoints = await IndexesAsync(MongoIndexes.ChargePoints);
        Assert.Single(chargePoints, i => i["key"].AsBsonDocument == new BsonDocument("_id", 1));

        var apiKeys = await IndexesAsync(MongoIndexes.ApiKeys);
        Assert.Single(apiKeys, i => i["key"].AsBsonDocument == new BsonDocument("_id", 1));
    }

    [Fact]
    public async Task Creates_telemetry_as_an_expiring_time_series()
    {
        var ct = TestContext.Current.CancellationToken;
        await MongoIndexes.EnsureAsync(_database, ct);

        var info = await CollectionInfoAsync(_database, MongoIndexes.Telemetry);
        Assert.Equal("timeseries", info["type"].AsString);
        var timeseries = info["options"]["timeseries"].AsBsonDocument;
        Assert.Equal("ts", timeseries["timeField"].AsString);
        Assert.Equal("meta", timeseries["metaField"].AsString);
        Assert.Equal("seconds", timeseries["granularity"].AsString);
        Assert.Equal(2_592_000, info["options"]["expireAfterSeconds"].ToInt64());

        var indexes = await IndexesAsync(MongoIndexes.Telemetry);
        Assert.Single(indexes, i => i["key"].AsBsonDocument == new BsonDocument { { "meta.deviceId", 1 }, { "ts", 1 } });
    }

    [Fact]
    public async Task An_ordinary_telemetry_collection_stops_startup()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName("wrongkind"));
        await database.CreateCollectionAsync(MongoIndexes.Telemetry, cancellationToken: ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MongoIndexes.EnsureAsync(database, ct));

        Assert.Contains("'telemetry'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_telemetry_retention_is_corrected()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName("retention"));
        await database.CreateCollectionAsync(
            MongoIndexes.Telemetry,
            new CreateCollectionOptions { TimeSeriesOptions = new TimeSeriesOptions("ts", "meta", TimeSeriesGranularity.Seconds), ExpireAfter = TimeSpan.FromDays(1) },
            ct);

        await MongoIndexes.EnsureAsync(database, ct);

        var info = await CollectionInfoAsync(database, MongoIndexes.Telemetry);
        Assert.Equal(2_592_000, info["options"]["expireAfterSeconds"].ToInt64());
    }

    [Fact]
    public async Task Inbox_records_expire_after_seven_days()
    {
        await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

        var inbox = await IndexesAsync(MongoIndexes.Inbox);
        var ttl = Assert.Single(inbox, i => i["key"].AsBsonDocument == new BsonDocument("handledAt", 1));
        Assert.Equal(604_800, ttl["expireAfterSeconds"].ToInt64());
    }

    private static async Task<BsonDocument> CollectionInfoAsync(IMongoDatabase database, string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var cursor = await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct);
        return Assert.Single(await cursor.ToListAsync(ct));
    }

    [Fact]
    public async Task Running_twice_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await MongoIndexes.EnsureAsync(_database, ct);
        var before = (await IndexesAsync(MongoIndexes.Devices)).Count + (await IndexesAsync(MongoIndexes.Connections)).Count;

        await MongoIndexes.EnsureAsync(_database, ct);

        var after = (await IndexesAsync(MongoIndexes.Devices)).Count + (await IndexesAsync(MongoIndexes.Connections)).Count;
        Assert.Equal(before, after);
    }
}
