using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Infrastructure;

public sealed class MongoFixtureTests(MongoFixture mongo)
{
    [Fact]
    public async Task Replica_set_answers_a_ping()
    {
        var database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(MongoFixtureTests)));

        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.0, reply["ok"].ToDouble());
    }

    [Fact]
    public async Task Replica_set_supports_transactions()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(MongoFixtureTests)));
        var collection = database.GetCollection<BsonDocument>("probe");
        await database.CreateCollectionAsync("probe", cancellationToken: ct);

        using var session = await mongo.Client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        await collection.InsertOneAsync(session, new BsonDocument("n", 1), cancellationToken: ct);
        await session.CommitTransactionAsync(ct);

        Assert.Equal(1, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct));
    }
}
