using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class LastReadingTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Consumer = "worker.telemetry";
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(LastReadingTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private TelemetryBatchWriter Writer => new(_database, _time);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<DeviceId> NewChargerAsync()
    {
        var device = new Device(
            DeviceId.New(_time), Owner, "ocpp", $"CP-{Guid.NewGuid():N}", DeviceType.Charger,
            Capabilities.ReadTelemetry, new DeviceAttributes(maxPowerW: 11000));
        return await new DeviceStateStore(_database).EnsureAsync(device, T0, TestContext.Current.CancellationToken);
    }

    private Task WriteAsync(DeviceId device, params DeviceReading[] readings) =>
        Writer.WriteAsync(Consumer, [new TelemetryBatchItem(Guid.CreateVersion7(), device, "ocpp", readings, IsRepeat: false)], TestContext.Current.CancellationToken);

    private static DeviceReading At(TimeSpan offset, long energyWh, int? powerW = null, decimal? soc = null) =>
        new("CP001", T0 + offset, powerW, energyWh, soc);

    private async Task<BsonDocument> LastReadingOf(DeviceId device)
    {
        var doc = await _database.GetCollection<BsonDocument>(MongoIndexes.Devices)
            .Find(new BsonDocument("_id", device.Value))
            .SingleAsync(TestContext.Current.CancellationToken);
        return doc["lastReading"].AsBsonDocument;
    }

    [Fact]
    public async Task Shuffled_readings_leave_the_latest_as_last_reading()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 100));
        await WriteAsync(device, At(TimeSpan.FromMinutes(2), 120));
        await WriteAsync(device, At(TimeSpan.FromMinutes(1), 110));

        var last = await LastReadingOf(device);
        Assert.Equal((T0 + TimeSpan.FromMinutes(2)).UtcDateTime, last["ts"].ToUniversalTime());
        Assert.Equal(120, last["energyWh"].ToInt64());
        Assert.Equal(3, await _database.GetCollection<BsonDocument>(MongoIndexes.Telemetry)
            .CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Value), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_late_reading_does_not_replace_a_newer_one()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.FromMinutes(5), 150, powerW: 7000));
        await WriteAsync(device, At(TimeSpan.FromMinutes(4), 140, powerW: 6000));

        var last = await LastReadingOf(device);
        Assert.Equal(150, last["energyWh"].ToInt64());
        Assert.Equal(7000, last["powerW"].ToInt32());
    }

    [Fact]
    public async Task The_latest_reading_of_a_batch_wins()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.FromSeconds(30), 130), At(TimeSpan.FromSeconds(45), 145), At(TimeSpan.FromSeconds(15), 115));

        Assert.Equal(145, (await LastReadingOf(device))["energyWh"].ToInt64());
    }

    [Fact]
    public async Task Missing_power_is_derived_from_two_energy_readings()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 1000));
        await WriteAsync(device, At(TimeSpan.FromSeconds(30), 1100, soc: 55m));

        var last = await LastReadingOf(device);
        Assert.Equal(12_000, last["powerW"].ToInt32());
        Assert.True(last["powerDerived"].AsBoolean);
        Assert.Equal(55.0, last["socPercent"].ToDouble());
        var stored = await _database.GetCollection<BsonDocument>(MongoIndexes.Telemetry)
            .Find(new BsonDocument { { "meta.deviceId", device.Value }, { "energyWh", 1100L } })
            .SingleAsync(ct);
        Assert.False(stored.Contains("powerW"));
    }

    [Fact]
    public async Task Reported_power_is_kept_and_not_marked_derived()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 1000));
        await WriteAsync(device, At(TimeSpan.FromSeconds(30), 1100, powerW: 7400));

        var last = await LastReadingOf(device);
        Assert.Equal(7400, last["powerW"].ToInt32());
        Assert.False(last["powerDerived"].AsBoolean);
    }

    [Fact]
    public async Task No_power_is_derived_across_a_gap_over_fifteen_minutes()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 1000));
        await WriteAsync(device, At(TimeSpan.FromMinutes(20), 2000));

        Assert.Equal(BsonNull.Value, (await LastReadingOf(device))["powerW"]);
    }

    [Fact]
    public async Task No_power_is_derived_across_a_meter_reset()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 5000));
        await WriteAsync(device, At(TimeSpan.FromSeconds(30), 10));

        Assert.Equal(BsonNull.Value, (await LastReadingOf(device))["powerW"]);
    }

    [Fact]
    public async Task The_first_reading_has_no_derived_power()
    {
        var device = await NewChargerAsync();

        await WriteAsync(device, At(TimeSpan.Zero, 1000));

        var last = await LastReadingOf(device);
        Assert.Equal(BsonNull.Value, last["powerW"]);
        Assert.False(last["powerDerived"].AsBoolean);
    }
}
