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
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(DeviceStateStoreTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private DeviceStateStore Store => new(_database);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<UpdateResult> SetAsync(string externalId, BsonDocument fields) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.Devices).UpdateOneAsync(
            new BsonDocument("externalId", externalId), new BsonDocument("$set", fields), cancellationToken: TestContext.Current.CancellationToken);

    private Task<BsonDocument> RawAsync(string externalId) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.Devices).Find(new BsonDocument("externalId", externalId)).SingleAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Removal_marks_the_device_offline_and_keeps_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP-RM1"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP-RM1", DeviceStatus.Charging, T0.AddSeconds(10), T0.AddSeconds(10), ct);

        Assert.True(await Store.MarkRemovedAsync(Provider, "CP-RM1", T0.AddMinutes(1), ct));

        var device = await Store.FindAsync(Provider, "CP-RM1", ct);
        Assert.Equal(DeviceStatus.Offline, device!.Status);
        Assert.Equal(T0.AddMinutes(1), device.State!.StatusAt);
        Assert.Equal(T0.AddMinutes(1).UtcDateTime, (await RawAsync("CP-RM1"))["removedAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Removal_older_than_the_status_keeps_the_status_but_records_the_removal()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP-RM2"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP-RM2", DeviceStatus.Charging, T0.AddMinutes(5), T0.AddMinutes(5), ct);

        Assert.False(await Store.MarkRemovedAsync(Provider, "CP-RM2", T0.AddMinutes(1), ct));

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP-RM2", ct))!.Status);
        Assert.Equal(T0.AddMinutes(1).UtcDateTime, (await RawAsync("CP-RM2"))["removedAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Rediscovery_clears_the_removal_and_the_status_follows_the_vendor_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = Charger("CP-RM3");
        var id = await Store.EnsureAsync(device, Interval, T0, ct);
        await Store.MarkRemovedAsync(Provider, "CP-RM3", T0.AddMinutes(1), ct);

        var again = await Store.EnsureAsync(Charger("CP-RM3"), Interval, T0.AddMinutes(2), ct);
        await Store.RecordStatusAsync(Provider, "CP-RM3", DeviceStatus.Available, T0.AddMinutes(2), T0.AddMinutes(2), ct);

        Assert.Equal(id, again);
        Assert.False((await RawAsync("CP-RM3")).Contains("removedAt"));
        Assert.Equal(DeviceStatus.Available, (await Store.FindAsync(Provider, "CP-RM3", ct))!.Status);
    }

    private Device Charger(string externalId) =>
        new(DeviceId.New(_time), Owner, Provider, externalId, DeviceType.Charger,
            Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit, new DeviceAttributes(maxPowerW: 11000));

    [Fact]
    public async Task First_connection_creates_the_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = Charger("CP001");

        var id = await Store.EnsureAsync(device, Interval, T0, ct);

        Assert.Equal(device.Id, id);
        var state = await Store.FindAsync(Provider, "CP001", ct);
        Assert.NotNull(state);
        Assert.Equal(Owner, state.OwnerId);
        Assert.Equal(DeviceType.Charger, state.Type);
        Assert.Equal(11000, state.Attributes.MaxPowerW);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit, state.Capabilities);
        Assert.Equal(DeviceStatus.Unknown, state.Status);
        Assert.Equal(T0, state.State!.StatusAt);
        Assert.Null(state.State.LastSeenAt);
        Assert.Equal(Interval, state.State.ReportInterval);
        Assert.Null(state.State.LastReading);
        var raw = await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).Find(new BsonDocument("externalId", "CP001")).SingleAsync(ct);
        Assert.Equal(T0.UtcDateTime, raw["createdAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Capabilities_and_enums_are_stored_as_names()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP010"), Interval, T0, ct);

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
        var first = await Store.EnsureAsync(Charger("CP002"), Interval, T0, ct);

        var second = await Store.EnsureAsync(Charger("CP002"), Interval, T0.AddMinutes(5), ct);

        Assert.Equal(first, second);
        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).CountDocumentsAsync(new BsonDocument("externalId", "CP002"), cancellationToken: ct));
    }

    [Fact]
    public async Task Concurrent_first_connections_create_one_device()
    {
        var ct = TestContext.Current.CancellationToken;

        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Store.EnsureAsync(Charger("CP003"), Interval, T0, ct)));

        Assert.Single(ids.Distinct());
        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).CountDocumentsAsync(new BsonDocument("externalId", "CP003"), cancellationToken: ct));
    }

    [Fact]
    public async Task Status_moves_only_forward_in_time()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP004"), Interval, T0, ct);

        Assert.True(await Store.RecordStatusAsync(Provider, "CP004", DeviceStatus.Charging, T0.AddMinutes(5), T0.AddMinutes(5), ct));
        Assert.False(await Store.RecordStatusAsync(Provider, "CP004", DeviceStatus.Available, T0.AddMinutes(1), T0.AddMinutes(1), ct));

        var state = await Store.FindAsync(Provider, "CP004", ct);
        Assert.Equal(DeviceStatus.Charging, state!.Status);
        Assert.Equal(T0.AddMinutes(5), state.State!.StatusAt);
    }

    [Fact]
    public async Task Report_from_just_before_a_reconnect_still_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP020"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP020", DeviceStatus.Available, T0.AddMinutes(1), T0.AddMinutes(1), ct);
        await Store.MarkOfflineAsync(Provider, "CP020", T0.AddMinutes(5), ct);
        await Store.EnsureAsync(Charger("CP020"), Interval, T0.AddMinutes(10), ct);

        Assert.True(await Store.RecordStatusAsync(Provider, "CP020", DeviceStatus.Charging, T0.AddMinutes(8), T0.AddMinutes(8), ct));

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP020", ct))!.Status);
    }

    [Fact]
    public async Task Offline_is_not_undone_by_an_older_report()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP021"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP021", DeviceStatus.Charging, T0.AddMinutes(1), T0.AddMinutes(1), ct);
        await Store.MarkOfflineAsync(Provider, "CP021", T0.AddMinutes(5), ct);

        Assert.False(await Store.RecordStatusAsync(Provider, "CP021", DeviceStatus.Available, T0.AddMinutes(3), T0.AddMinutes(3), ct));

        Assert.Equal(DeviceStatus.Offline, (await Store.FindAsync(Provider, "CP021", ct))!.Status);
    }

    [Fact]
    public async Task Offline_does_not_overwrite_a_newer_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP022"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP022", DeviceStatus.Charging, T0.AddMinutes(5), T0.AddMinutes(5), ct);

        Assert.False(await Store.MarkOfflineAsync(Provider, "CP022", T0.AddMinutes(4), ct));

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP022", ct))!.Status);
    }

    [Fact]
    public async Task Last_seen_never_moves_backwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP005"), Interval, T0, ct);

        await Store.RecordSeenAsync(Provider, "CP005", T0.AddMinutes(3), ct);
        await Store.RecordSeenAsync(Provider, "CP005", T0.AddMinutes(1), ct);

        Assert.Equal(T0.AddMinutes(3), (await Store.FindAsync(Provider, "CP005", ct))!.State!.LastSeenAt);
    }

    [Fact]
    public async Task Offline_device_becomes_unknown_when_it_reconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP006"), Interval, T0, ct);
        await Store.MarkOfflineAsync(Provider, "CP006", T0.AddMinutes(1), ct);

        await Store.EnsureAsync(Charger("CP006"), Interval, T0.AddMinutes(2), ct);

        var state = await Store.FindAsync(Provider, "CP006", ct);
        Assert.Equal(DeviceStatus.Unknown, state!.Status);
        Assert.Equal(T0.AddMinutes(2), state.State!.StatusAt);
    }

    [Fact]
    public async Task Reconnect_keeps_a_status_that_is_not_offline()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP007"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP007", DeviceStatus.Charging, T0.AddMinutes(1), T0.AddMinutes(1), ct);

        await Store.EnsureAsync(Charger("CP007"), Interval, T0.AddMinutes(2), ct);

        Assert.Equal(DeviceStatus.Charging, (await Store.FindAsync(Provider, "CP007", ct))!.Status);
    }

    [Fact]
    public async Task Hardware_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP008"), Interval, T0, ct);

        await Store.RecordHardwareAsync(Provider, "CP008", new DeviceHardware("Solidstudio", "VCP", "1.0"), ct);

        Assert.Equal(new DeviceHardware("Solidstudio", "VCP", "1.0"), (await Store.FindAsync(Provider, "CP008", ct))!.State!.Hardware);
    }

    [Fact]
    public async Task Last_reading_report_interval_and_battery_capacity_are_read_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var vehicle = new Device(DeviceId.New(_time), Owner, "carcloud", "VIN1", DeviceType.Vehicle, Capabilities.ReadStateOfCharge, new DeviceAttributes(batteryCapacityWh: 77_000));
        await Store.EnsureAsync(vehicle, Interval, T0, ct);
        await SetAsync("VIN1", new BsonDocument
        {
            { "reportIntervalSeconds", 300 },
            { "lastReading", new BsonDocument { { "ts", T0.AddMinutes(1).UtcDateTime }, { "energyWh", 1100L }, { "powerW", 12000 }, { "socPercent", 41.5 }, { "powerDerived", true } } },
        });

        var device = await Store.FindAsync("carcloud", "VIN1", ct);

        Assert.Equal(77_000, device!.Attributes.BatteryCapacityWh);
        Assert.Equal(TimeSpan.FromMinutes(5), device.State!.ReportInterval);
        Assert.Equal(new DeviceReading("VIN1", T0.AddMinutes(1), 12000, 1100, 41.5m), device.State.LastReading);
    }

    [Fact]
    public async Task Last_reading_with_unknown_values_reads_them_as_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP011"), Interval, T0, ct);
        await SetAsync("CP011", new BsonDocument(
            "lastReading",
            new BsonDocument { { "ts", T0.UtcDateTime }, { "energyWh", 12500L }, { "powerW", BsonNull.Value }, { "socPercent", BsonNull.Value }, { "powerDerived", false } }));

        var reading = (await Store.FindAsync(Provider, "CP011", ct))!.State!.LastReading;

        Assert.Equal(new DeviceReading("CP011", T0, null, 12500), reading);
    }

    [Fact]
    public async Task Every_connection_sets_the_report_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP012"), TimeSpan.FromSeconds(60), T0, ct);

        await Store.EnsureAsync(Charger("CP012"), TimeSpan.FromSeconds(120), T0.AddMinutes(5), ct);

        Assert.Equal(TimeSpan.FromSeconds(120), (await Store.FindAsync(Provider, "CP012", ct))!.State!.ReportInterval);
    }

    [Fact]
    public async Task Unknown_device_is_not_found()
    {
        Assert.Null(await Store.FindAsync(Provider, "CP999", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_change_stores_the_receipt_time_not_the_report_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await Store.EnsureAsync(Charger("CP030"), Interval, T0, ct);

        // The charger's clock runs two minutes behind: its report says 10:03, CheapHours received it at 10:05.
        await Store.RecordStatusAsync(Provider, "CP030", DeviceStatus.Charging, T0.AddMinutes(3), T0.AddMinutes(5), ct);

        var stored = await Commands.GetAsync(id, ct);
        Assert.Equal(T0.AddMinutes(5), stored!.StatusReceivedAt);
        Assert.Equal(T0.AddMinutes(3), stored.Device.State!.StatusAt);
    }

    [Fact]
    public async Task Stale_report_keeps_the_previous_receipt_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await Store.EnsureAsync(Charger("CP031"), Interval, T0, ct);
        await Store.RecordStatusAsync(Provider, "CP031", DeviceStatus.Charging, T0.AddMinutes(5), T0.AddMinutes(5), ct);

        await Store.RecordStatusAsync(Provider, "CP031", DeviceStatus.Available, T0.AddMinutes(1), T0.AddMinutes(6), ct);

        Assert.Equal(T0.AddMinutes(5), (await Commands.GetAsync(id, ct))!.StatusReceivedAt);
    }

    [Fact]
    public async Task Active_session_is_set_and_cleared_only_by_its_own_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.EnsureAsync(Charger("CP032"), Interval, T0, ct);

        await Commands.SetActiveSessionAsync(Provider, "CP032", "4242", ct);
        Assert.Equal("4242", (await Commands.FindAsync(Provider, "CP032", ct))!.ActiveSessionId);

        Assert.False(await Commands.ClearActiveSessionAsync(Provider, "CP032", "4241", ct));
        Assert.Equal("4242", (await Commands.FindAsync(Provider, "CP032", ct))!.ActiveSessionId);

        Assert.True(await Commands.ClearActiveSessionAsync(Provider, "CP032", "4242", ct));
        Assert.Null((await Commands.FindAsync(Provider, "CP032", ct))!.ActiveSessionId);
    }

    [Fact]
    public async Task Command_state_read_shows_the_removal_and_is_owner_scoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await Store.EnsureAsync(Charger("CP033"), Interval, T0, ct);
        await Store.MarkRemovedAsync(Provider, "CP033", T0.AddMinutes(1), ct);

        Assert.Equal(T0.AddMinutes(1), (await Commands.GetAsync(Owner, id, ct))!.RemovedAt);
        Assert.Null(await Commands.GetAsync(OwnerId.Parse("own_other"), id, ct));
        Assert.Null(await Commands.GetAsync(DeviceId.Parse("dev_missing"), ct));
    }

    private IDeviceCommandStateStore Commands => Store;
}
