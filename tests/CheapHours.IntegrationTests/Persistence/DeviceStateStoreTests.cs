using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class DeviceStateStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Provider = "ocpp";
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(DeviceStateStoreTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private DeviceStateStore Store => new(_database);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Device Charger(string externalId) =>
        new(DeviceId.New(_time), Owner, Provider, externalId, DeviceType.Charger,
            Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit, new DeviceAttributes(maxPowerW: 11000));

    [Fact]
    public async Task First_connection_creates_the_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = Charger("CP001");

        var id = await Store.EnsureAsync(device, T0, ct);

        Assert.Equal(device.Id, id);
        var state = await Store.FindAsync(Provider, "CP001", ct);
        Assert.NotNull(state);
        Assert.Equal(Owner, state.OwnerId);
        Assert.Equal(DeviceType.Charger, state.Type);
        Assert.Equal(11000, state.MaxPowerW);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit, state.Capabilities);
        Assert.Equal(DeviceStatus.Unknown, state.Status);
        Assert.Equal(T0, state.StatusAt);
        Assert.Equal(T0, state.CreatedAt);
        Assert.Null(state.LastSeenAt);
    }

    [Fact]
    public async Task Capabilities_and_enums_are_stored_as_names()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP010"), T0, ct);

        var raw = await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).Find(new BsonDocument("externalId", "CP010")).SingleAsync(ct);

        Assert.Equal(new BsonArray { "ReadTelemetry", "StartStop", "SetPowerLimit" }, raw["capabilities"]);
        Assert.Equal("Charger", raw["type"].AsString);
        Assert.Equal("Unknown", raw["status"].AsString);
        Assert.Equal(11000, raw["attributes"]["maxPowerW"].AsInt32);
        Assert.Equal(1, raw["schemaVersion"].AsInt32);
    }

    [Fact]
    public async Task Reconnection_reuses_the_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Store.EnsureAsync(Charger("CP002"), T0, ct);

        var second = await Store.EnsureAsync(Charger("CP002"), T0.AddMinutes(5), ct);

        Assert.Equal(first, second);
        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).CountDocumentsAsync(new BsonDocument("externalId", "CP002"), cancellationToken: ct));
    }

    [Fact]
    public async Task Concurrent_first_connections_create_one_device()
    {
        var ct = TestContext.Current.CancellationToken;

        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Store.EnsureAsync(Charger("CP003"), T0, ct)));

        Assert.Single(ids.Distinct());
        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).CountDocumentsAsync(new BsonDocument("externalId", "CP003"), cancellationToken: ct));
    }

    [Fact]
    public async Task Status_moves_only_forward_in_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP004"), T0, ct);

        Assert.True(await Store.RecordStatusAsync(Provider, "CP004", DeviceStatus.Charging, T0.AddMinutes(5), ct));
        Assert.False(await Store.RecordStatusAsync(Provider, "CP004", DeviceStatus.Available, T0.AddMinutes(1), ct));

        var state = await Store.FindAsync(Provider, "CP004", ct);
        Assert.Equal(DeviceStatus.Charging, state!.Status);
        Assert.Equal(T0.AddMinutes(5), state.StatusAt);
    }

    [Fact]
    public async Task Report_from_just_before_a_reconnect_still_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP020"), T0, ct);
        await Store.RecordStatusAsync(Provider, "CP020", DeviceStatus.Available, T0.AddMinutes(1), ct);
        await Store.MarkOfflineAsync(Provider, "CP020", T0.AddMinutes(5), ct);
        await Store.EnsureAsync(Charger("CP020"), T0.AddMinutes(10), ct);

        Assert.True(await Store.RecordStatusAsync(Provider, "CP020", DeviceStatus.Charging, T0.AddMinutes(8), ct));

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP020", ct))!.Status);
    }

    [Fact]
    public async Task Offline_is_not_undone_by_an_older_report()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP021"), T0, ct);
        await Store.RecordStatusAsync(Provider, "CP021", DeviceStatus.Charging, T0.AddMinutes(1), ct);
        await Store.MarkOfflineAsync(Provider, "CP021", T0.AddMinutes(5), ct);

        Assert.False(await Store.RecordStatusAsync(Provider, "CP021", DeviceStatus.Available, T0.AddMinutes(3), ct));

        Assert.Equal(DeviceStatus.Offline, (await Store.FindAsync(Provider, "CP021", ct))!.Status);
    }

    [Fact]
    public async Task Offline_does_not_overwrite_a_newer_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP022"), T0, ct);
        await Store.RecordStatusAsync(Provider, "CP022", DeviceStatus.Charging, T0.AddMinutes(5), ct);

        Assert.False(await Store.MarkOfflineAsync(Provider, "CP022", T0.AddMinutes(4), ct));

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP022", ct))!.Status);
    }

    [Fact]
    public async Task Last_seen_never_moves_backwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP005"), T0, ct);

        await Store.RecordSeenAsync(Provider, "CP005", T0.AddMinutes(3), ct);
        await Store.RecordSeenAsync(Provider, "CP005", T0.AddMinutes(1), ct);

        Assert.Equal(T0.AddMinutes(3), (await Store.FindAsync(Provider, "CP005", ct))!.LastSeenAt);
    }

    [Fact]
    public async Task Offline_device_becomes_unknown_when_it_reconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP006"), T0, ct);
        await Store.MarkOfflineAsync(Provider, "CP006", T0.AddMinutes(1), ct);

        await Store.EnsureAsync(Charger("CP006"), T0.AddMinutes(2), ct);

        var state = await Store.FindAsync(Provider, "CP006", ct);
        Assert.Equal(DeviceStatus.Unknown, state!.Status);
        Assert.Equal(T0.AddMinutes(2), state.StatusAt);
    }

    [Fact]
    public async Task Reconnect_keeps_a_status_that_is_not_offline()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP007"), T0, ct);
        await Store.RecordStatusAsync(Provider, "CP007", DeviceStatus.Charging, T0.AddMinutes(1), ct);

        await Store.EnsureAsync(Charger("CP007"), T0.AddMinutes(2), ct);

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP007", ct))!.Status);
    }

    [Fact]
    public async Task Hardware_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP008"), T0, ct);

        await Store.RecordHardwareAsync(Provider, "CP008", new DeviceHardware("Solidstudio", "VCP", "1.0"), ct);

        Assert.Equal(new DeviceHardware("Solidstudio", "VCP", "1.0"), (await Store.FindAsync(Provider, "CP008", ct))!.Hardware);
    }

    [Fact]
    public async Task Unknown_device_is_not_found()
    {
        Assert.Null(await Store.FindAsync(Provider, "CP999", TestContext.Current.CancellationToken));
    }
}
