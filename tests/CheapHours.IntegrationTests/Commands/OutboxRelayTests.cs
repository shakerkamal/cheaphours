using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// The outbox relay on a real worker host (command-dispatch spec: the relay publishes every saved message;
/// commands are accepted while the broker is down). Each test has its own database and broker prefix.
/// </summary>
public sealed class OutboxRelayTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private readonly string _database = MongoFixture.NewDatabaseName(nameof(OutboxRelayTests));
    private readonly string _prefix = RabbitMqFixture.NewPrefix(nameof(OutboxRelayTests));
    private IMongoDatabase _db = null!;

    private CommandStore Commands => new(_db);

    private IMongoCollection<BsonDocument> Outbox => _db.GetCollection<BsonDocument>(MongoIndexes.Outbox);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = mongo.Client.GetDatabase(_database);
        await MongoIndexes.EnsureAsync(_db, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // command-dispatch: "Worker stops between saving and publishing" (the relay half).
    [Fact]
    public async Task Entry_saved_while_no_worker_runs_is_published_once_a_worker_starts()
    {
        await using var observer = await EventsObserver.StartAsync(rabbit.ConnectionString, _prefix, CommandRequested.TypeName);
        var (command, announcement) = await SaveAsync("cold", DateTimeOffset.UtcNow);

        var host = await WorkerHost.StartAsync(mongo, rabbit, _database, _prefix, Settings("relay-0"));
        try
        {
            await WorkerHost.EventuallyAsync(observer.DrainAsync, n => n >= 1, Within);
            await WorkerHost.EventuallyAsync(() => StateAsync(command.Id), s => s == CommandState.Published, Within);
        }
        finally
        {
            await WorkerHost.StopAsync(host);
        }

        Assert.Equal([announcement.ToString()], observer.MessageIds);
        Assert.True(await PublishedAsync(announcement));
        Assert.Contains($"\"commandId\":\"{command.Id}\"", observer.Bodies[0], StringComparison.Ordinal);
    }

    // command-dispatch: "Publish not confirmed" and "Broker stopped" (the relay half).
    [Fact]
    public async Task Entries_wait_while_the_broker_is_down_and_are_published_once_when_it_is_back()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        var observer = await EventsObserver.StartAsync(broker.ConnectionString, _prefix, CommandRequested.TypeName);
        var host = await WorkerHost.StartAsync(mongo, broker.ConnectionString, _database, _prefix, Settings("relay-1"));
        try
        {
            await observer.DisposeAsync();
            await broker.StopAsync();
            var saved = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                saved.Add((await SaveAsync($"down{i}", DateTimeOffset.UtcNow)).Announcement);
            }

            // The relay keeps trying (with backoff) and marks nothing published.
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Equal(0, await Outbox.CountDocumentsAsync(new BsonDocument("published", true), cancellationToken: Ct));

            await broker.StartAsync();
            await WorkerHost.EventuallyAsync(() => Outbox.CountDocumentsAsync(new BsonDocument("published", true), cancellationToken: Ct), n => n == 3, TimeSpan.FromSeconds(60));

            observer = await EventsObserver.StartAsync(broker.ConnectionString, _prefix, CommandRequested.TypeName);
            await WorkerHost.EventuallyAsync(observer.DrainAsync, n => n >= 3, Within);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await observer.DrainAsync();
            Assert.Equal(saved.Select(id => id.ToString()).Order(), observer.MessageIds.Order());
            var attempts = await Outbox.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
            Assert.Contains(attempts, e => e["attempts"].AsInt32 > 0);
        }
        finally
        {
            await WorkerHost.StopAsync(host);
            await observer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Entry_due_later_waits_for_its_time()
    {
        await using var observer = await EventsObserver.StartAsync(rabbit.ConnectionString, _prefix, CommandRequested.TypeName);
        var host = await WorkerHost.StartAsync(mongo, rabbit, _database, _prefix, Settings("relay-2"));
        try
        {
            var (command, announcement) = await SaveAsync("later", DateTimeOffset.UtcNow.AddSeconds(4));

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Assert.Equal(0, await observer.DrainAsync());
            Assert.Equal(CommandState.Pending, await StateAsync(command.Id));

            await WorkerHost.EventuallyAsync(observer.DrainAsync, n => n == 1, Within);
            Assert.Equal([announcement.ToString()], observer.MessageIds);
        }
        finally
        {
            await WorkerHost.StopAsync(host);
        }
    }

    private static Dictionary<string, string> Settings(string replica) => new() { ["Worker:ReplicaId"] = replica };

    private async Task<(StoredCommand Command, Guid Announcement)> SaveAsync(string name, DateTimeOffset dueAt)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.Parse($"cmd_{name}{Guid.NewGuid():N}"), DeviceId.Parse("dev_relay"), CommandType.StartCharging, $"key_{name}_{Guid.NewGuid():N}"), now, dueAt),
            OwnerId.Parse("own_demo"),
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        var announcement = CommandOutbox.Announce(command, now);
        Assert.True(await Commands.CreateWithOutboxAsync(command, announcement, Ct));
        return (command, announcement.MessageId);
    }

    private async Task<CommandState> StateAsync(CommandId id) => (await Commands.FindAsync(id, Ct))!.Command.State;

    private async Task<bool> PublishedAsync(Guid messageId) =>
        (await Outbox.Find(new BsonDocument("_id", messageId.ToString())).SingleAsync(Ct))["published"].AsBoolean;
}
