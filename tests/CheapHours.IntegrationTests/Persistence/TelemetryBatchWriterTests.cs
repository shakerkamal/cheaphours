using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class TelemetryBatchWriterTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Consumer = "worker.telemetry";
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(TelemetryBatchWriterTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private TelemetryBatchWriter Writer => new(_database, _time);

    private IMongoCollection<BsonDocument> Telemetry => _database.GetCollection<BsonDocument>(MongoIndexes.Telemetry);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TelemetryBatchItem Item(Guid messageId, DeviceId device, IReadOnlyList<DeviceReading> readings, bool isRepeat = false) =>
        new(messageId, device, "ocpp", readings, isRepeat);

    private static DeviceReading Reading(int seconds, long energyWh, int? powerW = null, decimal? soc = null) =>
        new("CP001", T0.AddSeconds(seconds), powerW, energyWh, soc);

    private static DeviceId NewDevice() => DeviceId.Parse($"dev_{Guid.NewGuid():N}");

    private static BsonDocument RawReading(DeviceId device, int seconds, long energyWh, Guid messageId) => new()
    {
        { "ts", T0.AddSeconds(seconds).UtcDateTime },
        { "meta", new BsonDocument { { "deviceId", device.Value }, { "providerId", "ocpp" } } },
        { "energyWh", energyWh },
        { "messageId", messageId.ToString() },
    };

    private async Task<List<BsonDocument>> ReadingsOf(DeviceId device) =>
        await Telemetry.Find(new BsonDocument("meta.deviceId", device.Value)).Sort(new BsonDocument("ts", 1)).ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Energy_only_reading_is_stored_without_power_or_state_of_charge()
    {
        var device = NewDevice();
        var messageId = Guid.CreateVersion7();

        await Writer.WriteAsync(Consumer, [Item(messageId, device, [Reading(15, 12_500)])], TestContext.Current.CancellationToken);

        var doc = Assert.Single(await ReadingsOf(device));
        Assert.Equal(T0.AddSeconds(15).UtcDateTime, doc["ts"].ToUniversalTime());
        Assert.Equal(device.Value, doc["meta"]["deviceId"].AsString);
        Assert.Equal("ocpp", doc["meta"]["providerId"].AsString);
        Assert.Equal(12_500, doc["energyWh"].ToInt64());
        Assert.False(doc.Contains("powerW"));
        Assert.False(doc.Contains("socPercent"));
        Assert.Equal(messageId.ToString(), doc["messageId"].AsString);
    }

    [Fact]
    public async Task Reported_power_and_state_of_charge_are_stored()
    {
        var device = NewDevice();

        await Writer.WriteAsync(Consumer, [Item(Guid.CreateVersion7(), device, [Reading(0, 100, 7400, 61.5m)])], TestContext.Current.CancellationToken);

        var doc = Assert.Single(await ReadingsOf(device));
        Assert.Equal(7400, doc["powerW"].ToInt32());
        Assert.Equal(61.5, doc["socPercent"].ToDouble());
    }

    [Fact]
    public async Task A_duplicate_inside_a_batch_is_stored_once()
    {
        var device = NewDevice();
        var item = Item(Guid.CreateVersion7(), device, [Reading(0, 100), Reading(15, 110)]);

        await Writer.WriteAsync(Consumer, [item, item], TestContext.Current.CancellationToken);

        Assert.Equal(2, (await ReadingsOf(device)).Count);
    }

    [Fact]
    public async Task A_duplicate_across_batches_is_stored_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = NewDevice();
        var item = Item(Guid.CreateVersion7(), device, [Reading(0, 100)]);

        await Writer.WriteAsync(Consumer, [item], ct);
        await Writer.WriteAsync(Consumer, [item with { IsRepeat = true }], ct);
        await Writer.WriteAsync(Consumer, [item], ct);

        Assert.Single(await ReadingsOf(device));
    }

    [Fact]
    public async Task A_repeat_after_a_partial_write_is_stored_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = NewDevice();
        var messageId = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await Writer.WriteAsync(Consumer, [Item(other, device, [Reading(5, 95)])], ct);

        // A crash after the readings were written and before the inbox entry left them behind.
        await Telemetry.InsertManyAsync([RawReading(device, 0, 100, messageId), RawReading(device, 15, 110, messageId)], cancellationToken: ct);

        await Writer.WriteAsync(Consumer, [Item(messageId, device, [Reading(0, 100), Reading(15, 110)], isRepeat: true)], ct);

        var readings = await ReadingsOf(device);
        Assert.Equal([100L, 95L, 110L], readings.Select(r => r["energyWh"].ToInt64()));
        Assert.Equal(2, readings.Count(r => r["messageId"].AsString == messageId.ToString()));
    }

    [Fact]
    public async Task Handled_messages_are_recorded_in_the_inbox_per_consumer()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.CreateVersion7();

        await Writer.WriteAsync(Consumer, [Item(messageId, NewDevice(), [Reading(0, 1)])], ct);

        var inbox = await _database.GetCollection<BsonDocument>(MongoIndexes.Inbox).Find(new BsonDocument("_id", $"{Consumer}:{messageId}")).SingleAsync(ct);
        Assert.Equal(T0.UtcDateTime, inbox["handledAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Readings_for_a_missing_device_are_kept_and_create_no_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var gone = DeviceId.Parse("dev_gone");

        await Writer.WriteAsync(Consumer, [Item(Guid.CreateVersion7(), gone, [Reading(0, 1)])], ct);

        Assert.Single(await ReadingsOf(gone));
        Assert.Equal(0, await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).CountDocumentsAsync(new BsonDocument("_id", gone.Value), cancellationToken: ct));
    }
}
