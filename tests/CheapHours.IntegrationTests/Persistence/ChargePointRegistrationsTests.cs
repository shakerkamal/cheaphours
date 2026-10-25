using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.ChargePoints;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class ChargePointRegistrationsTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(ChargePointRegistrationsTests)));

    private ChargePointRegistrations Store => new(_database);

    private static ChargePointRegistration Registration(string id = "CP001", string? hash = null, int maxPowerW = 11000) =>
        new(id, OwnerId.Parse("own_demo"), hash ?? HashA, maxPowerW);

    [Fact]
    public async Task Seeded_registration_is_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.SeedAsync(Registration(), Day1, ct);

        var found = await Store.FindAsync("CP001", ct);

        Assert.NotNull(found);
        Assert.Equal(OwnerId.Parse("own_demo"), found.OwnerId);
        Assert.Equal(HashA, found.PasswordHash);
        Assert.Equal(11000, found.MaxPowerW);
        Assert.Equal(Day1, found.CreatedAt);
        Assert.False(found.IsRevoked);
    }

    [Fact]
    public async Task Seeding_twice_keeps_one_document_and_the_first_creation_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.SeedAsync(Registration(), Day1, ct);

        await Store.SeedAsync(Registration(hash: HashB, maxPowerW: 7400), Day1.AddDays(1), ct);

        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.ChargePoints).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct));
        var found = await Store.FindAsync("CP001", ct);
        Assert.Equal(Day1, found!.CreatedAt);
        Assert.Equal(HashB, found.PasswordHash);
        Assert.Equal(7400, found.MaxPowerW);
    }

    [Fact]
    public async Task Revocation_survives_a_reseed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.SeedAsync(Registration("CP002"), Day1, ct);
        await Store.RevokeAsync("CP002", Day1.AddHours(1), ct);

        await Store.SeedAsync(Registration("CP002"), Day1.AddDays(1), ct);

        var found = await Store.FindAsync("CP002", ct);
        Assert.True(found!.IsRevoked);
        Assert.Equal(Day1.AddHours(1), found.RevokedAt);
    }

    [Fact]
    public async Task Unknown_id_returns_none()
    {
        Assert.Null(await Store.FindAsync("CP999", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stored_field_names_follow_the_data_model()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.SeedAsync(Registration("CP003"), Day1, ct);

        var raw = await _database.GetCollection<BsonDocument>(MongoIndexes.ChargePoints).Find(new BsonDocument("_id", "CP003")).SingleAsync(ct);

        Assert.Equal(
            ["_id", "createdAt", "maxPowerW", "ownerId", "passwordHash", "schemaVersion"],
            raw.Names.Where(n => n != "revokedAt").Order(StringComparer.Ordinal));
        Assert.Equal(BsonType.DateTime, raw["createdAt"].BsonType);
    }
}
