using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Connections;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class ConnectionRegistryTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly ConnectionRegistry _registry = new(mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(ConnectionRegistryTests))));

    [Fact]
    public async Task Connect_records_the_replica()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = new ConnectionEntry("CP001", "gw-1", Guid.NewGuid(), T0);

        await _registry.RecordAsync(entry, ct);

        Assert.Equal(entry, await _registry.FindAsync("CP001", ct));
    }

    [Fact]
    public async Task Reconnect_elsewhere_is_not_erased_by_the_old_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = new ConnectionEntry("CP002", "gw-1", Guid.NewGuid(), T0);
        var newer = new ConnectionEntry("CP002", "gw-2", Guid.NewGuid(), T0.AddSeconds(5));
        await _registry.RecordAsync(old, ct);
        await _registry.RecordAsync(newer, ct);

        Assert.False(await _registry.RemoveIfCurrentAsync("CP002", old.ConnectionId, ct));

        Assert.Equal(newer, await _registry.FindAsync("CP002", ct));
    }

    [Fact]
    public async Task Current_connection_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = new ConnectionEntry("CP003", "gw-1", Guid.NewGuid(), T0);
        await _registry.RecordAsync(entry, ct);

        Assert.True(await _registry.RemoveIfCurrentAsync("CP003", entry.ConnectionId, ct));

        Assert.Null(await _registry.FindAsync("CP003", ct));
    }

    [Fact]
    public async Task Replica_sweep_removes_only_that_replicas_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        await _registry.RecordAsync(new ConnectionEntry("CP010", "gw-a", Guid.NewGuid(), T0), ct);
        await _registry.RecordAsync(new ConnectionEntry("CP011", "gw-a", Guid.NewGuid(), T0), ct);
        await _registry.RecordAsync(new ConnectionEntry("CP012", "gw-b", Guid.NewGuid(), T0), ct);

        Assert.Equal(2, await _registry.RemoveReplicaAsync("gw-a", ct));

        Assert.Null(await _registry.FindAsync("CP010", ct));
        Assert.Null(await _registry.FindAsync("CP011", ct));
        Assert.NotNull(await _registry.FindAsync("CP012", ct));
    }
}
