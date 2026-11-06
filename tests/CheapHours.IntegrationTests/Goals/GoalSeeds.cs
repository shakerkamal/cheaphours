using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Core.Prices;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Prices;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Goals;

/// <summary>Seeds devices, charges, prices and plan states straight into a test database.</summary>
internal static class GoalSeeds
{
    private static readonly FakeTimeProvider IdClock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly Lock IdLock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Seeds a vehicle (60 kWh) and a charger (11 kW) of <paramref name="owner"/>, the vehicle at <paramref name="socPercent"/>.</summary>
    public static async Task<(DeviceId Vehicle, DeviceId Charger)> CarAndWallboxAsync(IMongoDatabase database, OwnerId owner, string suffix, double? socPercent = 40.0, DateTimeOffset? readingAt = null)
    {
        // External ids are unique per call, so tests sharing a database never share a device.
        var unique = suffix + "-" + Guid.NewGuid().ToString("N")[..8];
        var vehicle = await DeviceAsync(database, owner, "carcloud", "veh-" + unique, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 60_000));
        var charger = await DeviceAsync(database, owner, "ocpp", "cp-" + unique, DeviceType.Charger, new DeviceAttributes(maxPowerW: 11_000));
        if (socPercent is { } soc)
        {
            await SetChargeAsync(database, vehicle, soc, readingAt ?? DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        return (vehicle, charger);
    }

    public static async Task<DeviceId> DeviceAsync(IMongoDatabase database, OwnerId owner, string provider, string externalId, DeviceType type, DeviceAttributes attributes)
    {
        DeviceId id;
        DateTimeOffset at;
        lock (IdLock)
        {
            IdClock.Advance(TimeSpan.FromMilliseconds(1));
            id = DeviceId.New(IdClock);
            at = IdClock.GetUtcNow();
        }

        var device = new Device(id, owner, provider, externalId, type, Capabilities.ReadTelemetry, attributes);
        return await new DeviceStateStore(database).EnsureAsync(device, TimeSpan.FromMinutes(1), at, Ct);
    }

    public static async Task SetChargeAsync(IMongoDatabase database, DeviceId vehicle, double socPercent, DateTimeOffset at) =>
        await database.GetCollection<BsonDocument>(MongoIndexes.Devices).UpdateOneAsync(
            new BsonDocument("_id", vehicle.Value),
            new BsonDocument("$set", new BsonDocument("lastReading", new BsonDocument
            {
                { "ts", at.UtcDateTime },
                { "energyWh", 0L },
                { "powerW", 0 },
                { "socPercent", socPercent },
            })),
            cancellationToken: Ct);

    /// <summary>Stores a price for every 15-minute slot from <paramref name="fromUtc"/> (rounded down) to <paramref name="toUtc"/>.</summary>
    public static async Task PricesAsync(IMongoDatabase database, DateTimeOffset fromUtc, DateTimeOffset toUtc, decimal price)
    {
        var slots = new List<PriceSlot>();
        for (var s = Core.SlotGrid.Floor(fromUtc); s < toUtc; s = s.AddMinutes(15))
        {
            slots.Add(new PriceSlot("DE-LU", s, price));
        }

        await new PriceStore(database).UpsertAsync(slots, DateTimeOffset.UtcNow, Ct);
    }

    /// <summary>Marks a plan version confirmed, as plan confirmation will.</summary>
    public static async Task ConfirmAsync(IMongoDatabase database, string versionId, DateTimeOffset at) =>
        await database.GetCollection<BsonDocument>(MongoIndexes.Plans).UpdateOneAsync(
            new BsonDocument("_id", versionId),
            new BsonDocument
            {
                { "$set", new BsonDocument { { "status", "Confirmed" }, { "confirmedAt", at.UtcDateTime } } },
                { "$unset", new BsonDocument("expiresAt", "") },
            },
            cancellationToken: Ct);

    /// <summary>Moves a draft's made and expiry times into the past, so it reads as expired.</summary>
    public static async Task ExpireAsync(IMongoDatabase database, string versionId) =>
        await database.GetCollection<BsonDocument>(MongoIndexes.Plans).UpdateOneAsync(
            new BsonDocument("_id", versionId),
            new BsonDocument("$set", new BsonDocument
            {
                { "madeAt", DateTime.UtcNow.AddMinutes(-30) },
                { "expiresAt", DateTime.UtcNow.AddMinutes(-20) },
            }),
            cancellationToken: Ct);
}
