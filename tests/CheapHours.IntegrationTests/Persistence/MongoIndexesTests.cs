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
        Assert.Single(devices, i => i["key"].AsBsonDocument == new BsonDocument("ownerId", 1));

        var connections = await IndexesAsync(MongoIndexes.Connections);
        Assert.Single(connections, i => i["key"].AsBsonDocument == new BsonDocument("replicaId", 1));

        var chargePoints = await IndexesAsync(MongoIndexes.ChargePoints);
        Assert.Single(chargePoints, i => i["key"].AsBsonDocument == new BsonDocument("_id", 1));
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
