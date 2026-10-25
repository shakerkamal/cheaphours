using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewayStartupTests(MongoFixture mongo)
{
    [Fact]
    public async Task Configured_registrations_are_seeded_with_the_hash_only()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();

        var cp = await gateway.Registrations.FindAsync("CP001", TestContext.Current.CancellationToken);

        Assert.NotNull(cp);
        Assert.Equal(GatewayFactory.Hash(GatewayFactory.Password), cp.PasswordHash);
        Assert.Equal(11000, cp.MaxPowerW);
        var raw = await mongo.Client.GetDatabase(gateway.DatabaseName).GetCollection<BsonDocument>(MongoIndexes.ChargePoints)
            .Find(new BsonDocument("_id", "CP001")).SingleAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(GatewayFactory.Password, raw.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_keeps_creation_time_and_revocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = MongoFixture.NewDatabaseName(nameof(GatewayStartupTests));
        DateTimeOffset? createdAt;

        await using (var first = new GatewayFactory(mongo.ConnectionString) { DatabaseName = database })
        {
            await first.StartAsync();
            createdAt = (await first.Registrations.FindAsync("CP002", ct))!.CreatedAt;
            await first.Registrations.RevokeAsync("CP002", DateTimeOffset.UtcNow, ct);
        }

        await using var second = new GatewayFactory(mongo.ConnectionString) { DatabaseName = database };
        await second.StartAsync();

        var cp = await second.Registrations.FindAsync("CP002", ct);
        Assert.Equal(createdAt, cp!.CreatedAt);
        Assert.True(cp.IsRevoked);
        Assert.Equal(2, await mongo.Client.GetDatabase(database).GetCollection<BsonDocument>(MongoIndexes.ChargePoints).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct));
    }

    [Fact]
    public async Task Invalid_hash_stops_startup_naming_the_charger_but_not_the_hash()
    {
        const string badHash = "not-a-sha256-hash-at-all";
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: new Dictionary<string, string?>
        {
            ["Providers:Ocpp:ChargePoints:0:PasswordHash"] = badHash,
        });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => gateway.StartAsync());

        var message = ex.ToString();
        Assert.Contains("CP001", message, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", message, StringComparison.Ordinal);
        Assert.DoesNotContain(badHash, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexes_exist_after_startup()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();

        using var cursor = await mongo.Client.GetDatabase(gateway.DatabaseName).GetCollection<BsonDocument>(MongoIndexes.Devices).Indexes.ListAsync(TestContext.Current.CancellationToken);
        var names = (await cursor.ToListAsync(TestContext.Current.CancellationToken)).Select(i => i["name"].AsString);

        Assert.Contains("providerId_externalId", names);
    }
}
