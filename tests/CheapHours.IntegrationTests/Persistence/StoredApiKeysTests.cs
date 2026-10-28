using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.ApiKeys;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class StoredApiKeysTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(StoredApiKeysTests)));

    private StoredApiKeys Keys => new(_database);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Hash(char c) => new(c, 64);

    [Fact]
    public async Task Seeded_key_is_found_by_its_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = new StoredApiKey(Hash('a'), "demo", "own_demo", ["read", "execute"]);

        await Keys.SeedAsync(key, T0, ct);

        var found = await Keys.FindByHashAsync(Hash('a'), ct);
        Assert.NotNull(found);
        Assert.Equal("demo", found.Name);
        Assert.Equal("own_demo", found.OwnerId);
        Assert.Equal(["read", "execute"], found.Scopes);
        var raw = await RawAsync(Hash('a'));
        Assert.Equal(T0.UtcDateTime, raw["createdAt"].ToUniversalTime());
        Assert.Equal(1, raw["schemaVersion"].AsInt32);
    }

    [Fact]
    public async Task Seeding_again_replaces_name_owner_and_scopes_but_keeps_the_creation_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await Keys.SeedAsync(new StoredApiKey(Hash('b'), "old", "own_demo", ["read"]), T0, ct);

        await Keys.SeedAsync(new StoredApiKey(Hash('b'), "renamed", "own_other", ["read", "execute"]), T0.AddDays(1), ct);

        var found = await Keys.FindByHashAsync(Hash('b'), ct);
        Assert.Equal(new StoredApiKey(Hash('b'), "renamed", "own_other", ["read", "execute"]).ToString(), found!.ToString());
        var raw = await RawAsync(Hash('b'));
        Assert.Equal(T0.UtcDateTime, raw["createdAt"].ToUniversalTime());
        Assert.Equal(T0.AddDays(1).UtcDateTime, raw["updatedAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Unknown_hash_is_not_found()
    {
        Assert.Null(await Keys.FindByHashAsync(Hash('f'), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Seeding_one_key_leaves_other_keys_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await Keys.SeedAsync(new StoredApiKey(Hash('c'), "kept", "own_demo", ["read"]), T0, ct);

        await Keys.SeedAsync(new StoredApiKey(Hash('d'), "new", "own_demo", ["read"]), T0, ct);

        Assert.Equal("kept", (await Keys.FindByHashAsync(Hash('c'), ct))!.Name);
    }

    [Fact]
    public void Description_never_contains_the_hash()
    {
        var key = new StoredApiKey(Hash('e'), "demo", "own_demo", ["read"]);

        Assert.DoesNotContain(Hash('e'), key.ToString(), StringComparison.Ordinal);
    }

    private Task<BsonDocument> RawAsync(string hash) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.ApiKeys).Find(new BsonDocument("_id", hash)).SingleAsync(TestContext.Current.CancellationToken);
}
