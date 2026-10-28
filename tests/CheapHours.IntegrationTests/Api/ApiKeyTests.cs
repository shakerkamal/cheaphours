using System.Net;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Api;

/// <summary>The public-api key scenarios against the real <c>apiKeys</c> collection.</summary>
public sealed class ApiKeyTests(MongoFixture mongo)
{
    private static readonly Uri Devices = new("/v1/devices", UriKind.Relative);

    private IMongoCollection<BsonDocument> Keys(ApiFactory api) =>
        mongo.Client.GetDatabase(api.DatabaseName).GetCollection<BsonDocument>(MongoIndexes.ApiKeys);

    private static async Task<HttpStatusCode> StatusAsync(ApiFactory api, string? key)
    {
        using var client = api.CreateClient(key);
        using var response = await client.GetAsync(Devices, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static BsonDocument KeyDocument(string key, string name, params string[] scopes) => new()
    {
        { "_id", ApiFactory.Hash(key) },
        { "name", name },
        { "ownerId", "own_a" },
        { "scopes", new BsonArray(scopes) },
        { "createdAt", DateTime.UtcNow },
        { "updatedAt", DateTime.UtcNow },
        { "schemaVersion", 1 },
    };

    [Fact]
    public async Task Configured_keys_are_seeded_on_an_empty_database()
    {
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();

        var stored = await Keys(api).Find(new BsonDocument("_id", ApiFactory.Hash(ApiFactory.ExecuteKeyA))).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("a-execute", stored["name"].AsString);
        Assert.Equal("own_a", stored["ownerId"].AsString);
        Assert.Equal(new BsonArray { "execute", "read" }, stored["scopes"]);
        Assert.Equal(3, await Keys(api).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Key_inserted_at_runtime_is_accepted_without_a_restart()
    {
        const string key = "ch_it_key_added_while_running";
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(api, key));

        await Keys(api).InsertOneAsync(KeyDocument(key, "added-later", "read"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, await StatusAsync(api, key));
    }

    [Fact]
    public async Task Key_added_in_the_database_survives_a_restart()
    {
        const string key = "ch_it_key_that_is_not_configured";
        string database;
        await using (var first = new ApiFactory(mongo.ConnectionString))
        {
            await first.StartAsync();
            database = first.DatabaseName;
            await Keys(first).InsertOneAsync(KeyDocument(key, "db-only", "read"), cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var restarted = new ApiFactory(mongo.ConnectionString) { DatabaseName = database };
        await restarted.StartAsync();

        Assert.NotEqual(HttpStatusCode.Unauthorized, await StatusAsync(restarted, key));
    }

    [Fact]
    public async Task Unknown_key_gets_401()
    {
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(api, "ch_it_key_nobody_stored"));
    }

    [Fact]
    public async Task Stored_key_with_an_unknown_scope_gets_401_and_is_logged_by_name_without_its_hash()
    {
        const string key = "ch_it_key_with_admin_scope";
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();
        await Keys(api).InsertOneAsync(KeyDocument(key, "too-powerful", "read", "admin"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(api, key));

        var line = Assert.Single(api.Logs.Lines, l => l.Contains("too-powerful", StringComparison.Ordinal));
        Assert.Contains("admin", line, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(ApiFactory.Hash(key), StringComparison.Ordinal));
    }
}
