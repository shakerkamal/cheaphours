using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Outbox;
using MongoDB.Bson;
using static CheapHours.IntegrationTests.Persistence.CommandSamples;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// The <c>outbox</c> collection (command-dispatch design §2–3). Each test uses its own database, because the due
/// listing is global.
/// </summary>
public sealed class OutboxStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private OutboxStore Store => new(_database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(OutboxStoreTests)));
        await MongoIndexes.EnsureAsync(_database, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Entry_added_in_a_session_round_trips()
    {
        var entry = Announcement(Command(_s));

        await AddAsync(entry);

        Assert.Equal([entry], await Store.ListDueAsync(Now, 10, Ct));
    }

    [Fact]
    public async Task Entry_added_in_an_aborted_transaction_does_not_exist()
    {
        using var session = await mongo.Client.StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();
        await OutboxStore.AddAsync(OutboxStore.Collection(_database), session, [Announcement(Command(_s))], Ct);
        await session.AbortTransactionAsync(Ct);

        Assert.Empty(await Store.ListDueAsync(Now, 10, Ct));
    }

    [Fact]
    public async Task Due_listing_is_oldest_first_and_leaves_out_later_and_published_entries()
    {
        var older = Announcement(Command(_s, "older"), availableAt: Now.AddSeconds(-20));
        var newer = Announcement(Command(_s, "newer"), availableAt: Now.AddSeconds(-10));
        var later = Announcement(Command(_s, "later"), availableAt: Now.AddMinutes(5));
        var published = Announcement(Command(_s, "done"), availableAt: Now.AddSeconds(-30));
        await AddAsync(newer, later, older, published);
        await Store.MarkPublishedAsync(published.MessageId, Now, Ct);

        var due = await Store.ListDueAsync(Now, 10, Ct);

        Assert.Equal([older.MessageId, newer.MessageId], due.Select(e => e.MessageId));
        Assert.Single(await Store.ListDueAsync(Now, 1, Ct));
    }

    [Fact]
    public async Task Marking_published_happens_once()
    {
        var entry = Announcement(Command(_s));
        await AddAsync(entry);

        Assert.True(await Store.MarkPublishedAsync(entry.MessageId, Now, Ct));
        Assert.False(await Store.MarkPublishedAsync(entry.MessageId, Now.AddSeconds(1), Ct));

        var document = await Documents.Find(new BsonDocument("_id", entry.MessageId.ToString())).SingleAsync(Ct);
        Assert.Equal(Now.UtcDateTime, document["publishedAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Failed_attempt_is_counted_and_the_entry_stays_due()
    {
        var entry = Announcement(Command(_s));
        await AddAsync(entry);

        await Store.RecordFailedAttemptAsync(entry.MessageId, Ct);
        await Store.RecordFailedAttemptAsync(entry.MessageId, Ct);

        Assert.Equal(2, Assert.Single(await Store.ListDueAsync(Now, 10, Ct)).Attempts);
    }

    [Fact]
    public async Task Deleting_by_command_removes_only_its_unpublished_entries()
    {
        var command = Command(_s, "gone");
        var unpublished = Announcement(command);
        var published = Announcement(command);
        var other = Announcement(Command(_s, "kept"));
        await AddAsync(unpublished, published, other);
        await Store.MarkPublishedAsync(published.MessageId, Now, Ct);

        using (var session = await mongo.Client.StartSessionAsync(cancellationToken: Ct))
        {
            await session.WithTransactionAsync(
                async (s, ct) =>
                {
                    await OutboxStore.DeleteUnpublishedAsync(OutboxStore.Collection(_database), s, [command.Id, CommandId.Parse($"cmd_none{_s}")], ct);
                    return true;
                },
                cancellationToken: Ct);
        }

        var documents = await Documents.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
        Assert.Equal(new[] { published.MessageId.ToString(), other.MessageId.ToString() }.Order(), documents.Select(d => d["_id"].AsString).Order());
    }

    [Fact]
    public async Task Indexes_cover_the_due_listing_and_expire_published_entries()
    {
        var indexes = await (await Documents.Indexes.ListAsync(Ct)).ToListAsync(Ct);

        var due = Assert.Single(indexes, i => i["name"] == "unpublished_availableAt");
        Assert.Equal(new BsonDocument("published", false), due["partialFilterExpression"].AsBsonDocument);
        var ttl = Assert.Single(indexes, i => i["key"].AsBsonDocument == new BsonDocument("publishedAt", 1));
        Assert.Equal((long)TimeSpan.FromDays(7).TotalSeconds, ttl["expireAfterSeconds"].ToInt64());
    }

    private IMongoCollection<BsonDocument> Documents => _database.GetCollection<BsonDocument>(MongoIndexes.Outbox);

    private async Task AddAsync(params OutboxEntry[] entries)
    {
        using var session = await mongo.Client.StartSessionAsync(cancellationToken: Ct);
        await session.WithTransactionAsync(
            async (s, ct) =>
            {
                await OutboxStore.AddAsync(OutboxStore.Collection(_database), s, entries, ct);
                return true;
            },
            cancellationToken: Ct);
    }
}
