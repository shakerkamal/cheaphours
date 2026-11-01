using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary><c>providerAccounts</c>: seeding, compare-and-set tokens, the reconnect mark and the poll schedule.</summary>
public sealed class ProviderAccountStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(ProviderAccountStoreTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private ProviderAccountStore Store => new(_database, _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ProviderAccount Account(string id, string owner = "own_demo", string provider = "cloudx") =>
        new(provider, OwnerId.Parse(owner), id);

    private Task<BsonDocument> RawAsync(ProviderAccount account) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.ProviderAccounts)
            .Find(new BsonDocument("_id", $"{account.ProviderId}/{account.ExternalAccountId}")).SingleAsync(Ct);

    [Fact]
    public async Task Seeding_inserts_a_missing_account()
    {
        var account = Account("acct_1");

        Assert.Equal(SeedOutcome.Inserted, await Store.SeedAsync(account, "blob-seed", Ct));

        Assert.Equal(new StoredAccountTokens("blob-seed", 1, ReconnectNeeded: false), await Store.ReadTokensAsync(account, Ct));
        var raw = await RawAsync(account);
        Assert.Equal("own_demo", raw["ownerId"].AsString);
        Assert.Equal(T0.UtcDateTime, raw["createdAt"].ToUniversalTime());
        Assert.False(raw.Contains("nextPollAt"));
        Assert.Equal(1, raw["schemaVersion"].AsInt32);
    }

    [Fact]
    public async Task Seeding_again_keeps_rotated_tokens()
    {
        var account = Account("acct_2", owner: "own_2");
        await Store.SeedAsync(account, "blob-seed", Ct);
        Assert.True(await Store.TryWriteTokensAsync(account, "blob-rotated", 1, Ct));

        Assert.Equal(SeedOutcome.Unchanged, await Store.SeedAsync(account, "blob-seed", Ct));

        Assert.Equal(new StoredAccountTokens("blob-rotated", 2, ReconnectNeeded: false), await Store.ReadTokensAsync(account, Ct));
    }

    [Fact]
    public async Task Seeding_relinks_an_account_that_needs_reconnecting()
    {
        var account = Account("acct_3", owner: "own_3");
        await Store.SeedAsync(account, "blob-seed", Ct);
        await Store.RecordPollAsync(account, T0.AddMinutes(1), T0.AddMinutes(5), Ct);
        await Store.MarkReconnectNeededAsync(account, Ct);

        Assert.Equal(SeedOutcome.Relinked, await Store.SeedAsync(account, "blob-new-seed", Ct));

        Assert.Equal(new StoredAccountTokens("blob-new-seed", 2, ReconnectNeeded: false), await Store.ReadTokensAsync(account, Ct));
        var raw = await RawAsync(account);
        Assert.False(raw.Contains("nextPollAt"));
        Assert.False(raw.Contains("pausedUntil"));
    }

    [Fact]
    public async Task A_write_with_a_stale_version_fails()
    {
        var account = Account("acct_4", owner: "own_4");
        await Store.SeedAsync(account, "blob-seed", Ct);
        Assert.True(await Store.TryWriteTokensAsync(account, "blob-a", 1, Ct));

        Assert.False(await Store.TryWriteTokensAsync(account, "blob-b", 1, Ct));

        Assert.Equal("blob-a", (await Store.ReadTokensAsync(account, Ct))!.ProtectedTokens);
    }

    [Fact]
    public async Task Concurrent_writes_from_one_version_let_exactly_one_win()
    {
        var account = Account("acct_5", owner: "own_5");
        await Store.SeedAsync(account, "blob-seed", Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store.TryWriteTokensAsync(account, $"blob-{i}", 1, Ct)));

        Assert.Single(results, r => r);
        Assert.Equal(2, (await Store.ReadTokensAsync(account, Ct))!.Version);
    }

    [Fact]
    public async Task The_raw_document_holds_no_token_as_text()
    {
        const string AccessToken = "access-token-5b1e";
        const string RefreshToken = "refresh-token-77aa";
        var protector = new EphemeralDataProtectionProvider().CreateProtector("CheapHours.Providers.Tokens.v1");
        var account = Account("acct_6", owner: "own_6");
        await Store.SeedAsync(account, protector.Protect($$"""{"refreshToken":"{{RefreshToken}}"}"""), Ct);
        await Store.TryWriteTokensAsync(account, protector.Protect($$"""{"accessToken":"{{AccessToken}}","refreshToken":"{{RefreshToken}}"}"""), 1, Ct);

        var raw = (await RawAsync(account)).ToJson();

        Assert.DoesNotContain(AccessToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_links_one_account_per_provider()
    {
        await Store.SeedAsync(Account("acct_7", owner: "own_x"), "blob", Ct);

        await Assert.ThrowsAsync<MongoWriteException>(() => Store.SeedAsync(Account("acct_8", owner: "own_x"), "blob", Ct));
    }

    [Fact]
    public async Task Due_listing_skips_reconnect_needed_paused_excluded_and_not_yet_due_accounts()
    {
        var provider = "cloudy";
        ProviderAccount Make(string id) => Account(id, owner: $"own_{id}", provider: provider);
        var fresh = Make("fresh");
        var due = Make("due");
        var later = Make("later");
        var paused = Make("paused");
        var broken = Make("broken");
        var inFlight = Make("inflight");
        foreach (var account in new[] { fresh, due, later, paused, broken, inFlight })
        {
            await Store.SeedAsync(account, "blob", Ct);
        }

        await Store.RecordPollAsync(due, T0.AddSeconds(-30), null, Ct);
        await Store.RecordPollAsync(later, T0.AddSeconds(30), null, Ct);
        await Store.RecordPollAsync(paused, T0.AddSeconds(-30), T0.AddMinutes(2), Ct);
        await Store.MarkReconnectNeededAsync(broken, Ct);
        await Store.SeedAsync(Account("other", owner: "own_other", provider: "elsewhere"), "blob", Ct);

        var listed = await Store.ListDueAsync(provider, T0, ["inflight"], 10, Ct);

        Assert.Equal(["fresh", "due"], listed.Select(a => a.ExternalAccountId));
        Assert.Equal(OwnerId.Parse("own_fresh"), listed[0].OwnerId);
        Assert.Equal(provider, listed[0].ProviderId);
        Assert.Single(await Store.ListDueAsync(provider, T0, [], 1, Ct));
    }

    [Fact]
    public async Task A_pause_ends_when_its_time_comes()
    {
        var account = Account("acct_p", owner: "own_p", provider: "pausing");
        await Store.SeedAsync(account, "blob", Ct);
        await Store.RecordPollAsync(account, T0.AddSeconds(60), T0.AddSeconds(120), Ct);

        Assert.Empty(await Store.ListDueAsync("pausing", T0.AddSeconds(90), [], 10, Ct));
        Assert.Single(await Store.ListDueAsync("pausing", T0.AddSeconds(120), [], 10, Ct));
    }

    [Fact]
    public async Task Schedule_writes_set_and_clear_the_pause()
    {
        var account = Account("acct_s", owner: "own_s");
        await Store.SeedAsync(account, "blob", Ct);

        await Store.RecordPollAsync(account, T0.AddMinutes(2), T0.AddMinutes(2), Ct);
        var paused = await RawAsync(account);
        await Store.RecordPollAsync(account, T0.AddMinutes(3), null, Ct);
        var resumed = await RawAsync(account);

        Assert.Equal(T0.AddMinutes(2).UtcDateTime, paused["nextPollAt"].ToUniversalTime());
        Assert.Equal(T0.AddMinutes(2).UtcDateTime, paused["pausedUntil"].ToUniversalTime());
        Assert.Equal(T0.AddMinutes(3).UtcDateTime, resumed["nextPollAt"].ToUniversalTime());
        Assert.False(resumed.Contains("pausedUntil"));
    }

    [Fact]
    public async Task Unknown_account_has_no_tokens()
    {
        Assert.Null(await Store.ReadTokensAsync(Account("nobody"), Ct));
    }
}
