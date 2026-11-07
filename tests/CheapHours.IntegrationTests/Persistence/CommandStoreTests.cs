using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using MongoDB.Bson;
using static CheapHours.IntegrationTests.Persistence.CommandSamples;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// The <c>commands</c> collection (command-dispatch design §2): round trips through Core, optimistic concurrency,
/// claims and the unique idempotency key. The tests share one database and keep apart by a per-test suffix.
/// </summary>
public sealed class CommandStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private CommandStore Commands => new(_database);

    private IMongoCollection<BsonDocument> Outbox => _database.GetCollection<BsonDocument>(MongoIndexes.Outbox);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(CommandStoreTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Command_round_trips_with_its_history_and_bookkeeping()
    {
        var command = Command(_s, dueAt: Now.AddHours(3), planId: PlanId.Parse("plan_8812"), planVersion: 2);
        Assert.True(await Commands.CreateWithOutboxAsync(command, Announcement(command), Ct));
        var loaded = await Commands.FindAsync(command.Id, Ct);
        loaded!.Command.Publish(Now.AddSeconds(1));
        loaded.Command.RetryLater(Now.AddSeconds(2), Now.AddSeconds(30), "vendor returned 429");
        var saved = await Commands.SaveAsync(loaded with { Announcements = 1, LastAnnouncedAt = Now.AddSeconds(2), AlreadyInEffect = true }, Ct);

        var read = await Commands.GetAsync(Owner, command.Id, Ct);

        Assert.NotNull(saved);
        Assert.Equal(saved.Version, read!.Version);
        Assert.Equal(command.Command.Request, read.Command.Request);
        Assert.Equal(Now.AddHours(3), read.Command.DueAt);
        Assert.Equal(CommandState.Published, read.Command.State);
        Assert.Equal(1, read.Command.Attempts);
        Assert.Equal(Now.AddSeconds(30), read.Command.NextAttemptAt);
        Assert.Equal(loaded.Command.History, read.Command.History);
        Assert.Equal((PlanId.Parse("plan_8812"), 2), (read.PlanId!.Value, read.PlanVersion!.Value));
        Assert.Equal(command.Traceparent, read.Traceparent);
        Assert.Equal((1, Now.AddSeconds(2), true), (read.Announcements, read.LastAnnouncedAt, read.AlreadyInEffect));
    }

    [Fact]
    public async Task Creating_stores_the_command_pending_with_its_outbox_entry()
    {
        var command = Command(_s);
        var announcement = Announcement(command);

        await Commands.CreateWithOutboxAsync(command, announcement, Ct);

        Assert.Equal(CommandState.Pending, (await Commands.FindAsync(command.Id, Ct))!.Command.State);
        var entry = await Outbox.Find(new BsonDocument("commandId", command.Id.Value)).SingleAsync(Ct);
        Assert.Equal(announcement.MessageId.ToString(), entry["_id"].AsString);
        Assert.False(entry["published"].AsBoolean);
    }

    [Fact]
    public async Task Another_owner_cannot_read_the_command()
    {
        var command = Command(_s);
        await Commands.CreateWithOutboxAsync(command, Announcement(command), Ct);

        Assert.Null(await Commands.GetAsync(OwnerId.Parse("own_other"), command.Id, Ct));
    }

    [Fact]
    public async Task Save_against_an_old_version_writes_nothing()
    {
        var command = Command(_s);
        await Commands.CreateWithOutboxAsync(command, Announcement(command), Ct);
        var first = (await Commands.FindAsync(command.Id, Ct))!;
        var second = (await Commands.FindAsync(command.Id, Ct))!;
        first.Command.Publish(Now.AddSeconds(1));
        second.Command.Cancel(Now.AddSeconds(1), "superseded");

        Assert.NotNull(await Commands.SaveAsync(first, Ct));
        Assert.Null(await Commands.SaveAsync(second, Ct));

        Assert.Equal(CommandState.Published, (await Commands.FindAsync(command.Id, Ct))!.Command.State);
    }

    [Fact]
    public async Task Second_command_with_the_same_idempotency_key_is_refused_with_nothing_written()
    {
        var first = Command(_s);
        await Commands.CreateWithOutboxAsync(first, Announcement(first), Ct);
        var request = first.Command.Request;
        var copy = new StoredCommand(
            Core.Commands.Command.Create(new DeviceCommand(CommandId.Parse($"cmd_copy{_s}"), request.DeviceId, request.Type, request.IdempotencyKey), Now),
            Owner,
            null);

        Assert.False(await Commands.CreateWithOutboxAsync(copy, Announcement(copy), Ct));

        Assert.Null(await Commands.FindAsync(copy.Id, Ct));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(new BsonDocument("commandId", copy.Id.Value), cancellationToken: Ct));
    }

    [Fact]
    public async Task Two_concurrent_claims_have_exactly_one_winner()
    {
        var command = await PublishedAsync(Command(_s));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Commands.TryClaimAsync(command.Id, $"worker-{i}", Now.AddSeconds(5), TimeSpan.FromSeconds(45), Ct)));

        var winner = Assert.Single(claims, c => c is not null);
        Assert.Equal(Now.AddSeconds(50), winner!.Claim!.Until);
        Assert.Equal(winner.Claim, (await Commands.FindAsync(command.Id, Ct))!.Claim);
    }

    [Fact]
    public async Task Command_that_is_not_due_or_waiting_for_a_retry_cannot_be_claimed()
    {
        var later = await PublishedAsync(Command(_s, "later", dueAt: Now.AddHours(1)));
        var retrying = await PublishedAsync(Command(_s, "retry"));
        retrying.Command.RetryLater(Now.AddSeconds(2), Now.AddSeconds(30), "vendor returned 503");
        await Commands.SaveAsync(retrying, Ct);

        Assert.Null(await Commands.TryClaimAsync(later.Id, "w", Now.AddSeconds(5), TimeSpan.FromSeconds(45), Ct));
        Assert.Null(await Commands.TryClaimAsync(retrying.Id, "w", Now.AddSeconds(5), TimeSpan.FromSeconds(45), Ct));
        Assert.NotNull(await Commands.TryClaimAsync(retrying.Id, "w", Now.AddSeconds(30), TimeSpan.FromSeconds(45), Ct));
    }

    [Fact]
    public async Task Lapsed_claim_can_be_taken_over_and_expired()
    {
        var command = await PublishedAsync(Command(_s));
        await Commands.TryClaimAsync(command.Id, "w1", Now.AddSeconds(5), TimeSpan.FromSeconds(45), Ct);

        Assert.Null(await Commands.TryClaimAsync(command.Id, "w2", Now.AddSeconds(49), TimeSpan.FromSeconds(45), Ct));
        Assert.NotNull(await Commands.TryClaimAsync(command.Id, "w2", Now.AddSeconds(50), TimeSpan.FromSeconds(45), Ct));

        Assert.True(await Commands.ExpireClaimsAsync(Now.AddSeconds(95), Ct) >= 1);
        Assert.Null((await Commands.FindAsync(command.Id, Ct))!.Claim);
    }

    [Fact]
    public async Task Release_drops_only_the_claimants_own_claim()
    {
        var command = await PublishedAsync(Command(_s));
        await Commands.TryClaimAsync(command.Id, "w1", Now.AddSeconds(5), TimeSpan.FromSeconds(45), Ct);

        Assert.False(await Commands.ReleaseClaimAsync(command.Id, "w2", Ct));
        Assert.True(await Commands.ReleaseClaimAsync(command.Id, "w1", Ct));

        Assert.Null((await Commands.FindAsync(command.Id, Ct))!.Claim);
    }

    [Fact]
    public async Task Sendable_listing_holds_due_unclaimed_published_commands_in_due_order()
    {
        var device = $"dev_list{_s}";
        var second = await PublishedAsync(Command(_s, "second", dueAt: Now.AddSeconds(-10), device: device));
        var first = await PublishedAsync(Command(_s, "first", dueAt: Now.AddSeconds(-20), device: device));
        await PublishedAsync(Command(_s, "future", dueAt: Now.AddHours(1), device: device));
        var claimed = await PublishedAsync(Command(_s, "claimed", dueAt: Now.AddSeconds(-30), device: device));
        await Commands.TryClaimAsync(claimed.Id, "w", Now, TimeSpan.FromSeconds(45), Ct);
        var pending = Command(_s, "pending", device: device);
        await Commands.CreateWithOutboxAsync(pending, Announcement(pending), Ct);

        var sendable = (await Commands.ListSendableAsync(Now.AddSeconds(1), 500, Ct)).Where(c => c.Command.Request.DeviceId.Value == device);

        Assert.Equal([first.Id, second.Id], sendable.Select(c => c.Id));
    }

    [Fact]
    public async Task Open_listing_for_a_device_leaves_out_terminal_commands()
    {
        var device = $"dev_open{_s}";
        var open = await PublishedAsync(Command(_s, "open", device: device));
        var cancelled = Command(_s, "gone", device: device);
        await Commands.CreateWithOutboxAsync(cancelled, Announcement(cancelled), Ct);
        var loaded = (await Commands.FindAsync(cancelled.Id, Ct))!;
        loaded.Command.Cancel(Now.AddSeconds(1), "superseded");
        await Commands.SaveAsync(loaded, Ct);

        var listed = await Commands.ListOpenForDeviceAsync(DeviceId.Parse(device), Ct);

        Assert.Equal([open.Id], listed.Select(c => c.Id));
    }

    [Fact]
    public async Task Plan_version_listing_returns_its_commands()
    {
        var plan = PlanId.Parse($"plan_l{_s}");
        var start = Command(_s, "start", dueAt: Now.AddHours(1), planId: plan, planVersion: 1);
        var stop = Command(_s, "stop", CommandType.StopCharging, Now.AddHours(2), planId: plan, planVersion: 1);
        var other = Command(_s, "other", dueAt: Now.AddHours(1), planId: plan, planVersion: 2);
        foreach (var c in new[] { stop, start, other })
        {
            await Commands.CreateWithOutboxAsync(c, Announcement(c), Ct);
        }

        Assert.Equal([start.Id, stop.Id], (await Commands.ListForPlanVersionAsync(plan, 1, Ct)).Select(c => c.Id));
    }

    [Fact]
    public async Task Save_with_outbox_against_an_old_version_writes_neither()
    {
        var command = await PublishedAsync(Command(_s));
        var stale = command with { Version = command.Version - 1 };
        var announcement = Announcement(command);

        Assert.Null(await Commands.SaveWithOutboxAsync(stale with { Announcements = 1 }, announcement, Ct));

        Assert.Equal(0, await Outbox.CountDocumentsAsync(new BsonDocument("_id", announcement.MessageId.ToString()), cancellationToken: Ct));
        Assert.NotNull(await Commands.SaveWithOutboxAsync(command with { Announcements = 1 }, announcement, Ct));
        Assert.Equal(1, await Outbox.CountDocumentsAsync(new BsonDocument("_id", announcement.MessageId.ToString()), cancellationToken: Ct));
    }

    [Fact]
    public async Task Document_whose_state_disagrees_with_its_history_is_refused()
    {
        var command = Command(_s);
        await Commands.CreateWithOutboxAsync(command, Announcement(command), Ct);
        await _database.GetCollection<BsonDocument>(MongoIndexes.Commands).UpdateOneAsync(
            new BsonDocument("_id", command.Id.Value),
            new BsonDocument("$set", new BsonDocument("state", "Sent")),
            cancellationToken: Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Commands.FindAsync(command.Id, Ct));
    }

    private async Task<StoredCommand> PublishedAsync(StoredCommand command)
    {
        await Commands.CreateWithOutboxAsync(command, Announcement(command), Ct);
        var loaded = (await Commands.FindAsync(command.Id, Ct))!;
        loaded.Command.Publish(Now.AddSeconds(1));
        return (await Commands.SaveAsync(loaded, Ct))!;
    }
}
