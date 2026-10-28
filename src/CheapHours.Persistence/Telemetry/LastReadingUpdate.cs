using CheapHours.Core.Devices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Telemetry;

/// <summary>
/// Builds the one-round-trip update that moves a device's <c>lastReading</c> forward. It changes nothing
/// unless the reading is newer than the stored one, so it is safe to apply twice and in any order. A
/// reading without power gets power derived from the energy added since the stored reading.
/// </summary>
internal static class LastReadingUpdate
{
    /// <summary>The longest gap between two readings that power is still derived across.</summary>
    public static readonly TimeSpan MaxDerivationGap = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan MinDerivationGap = TimeSpan.FromSeconds(1);

    /// <summary>Builds the update for <paramref name="reading"/>.</summary>
    /// <param name="reading">The device's newest reading in the batch.</param>
    /// <returns>A pipeline update for the device document.</returns>
    public static UpdateDefinition<BsonDocument> For(DeviceReading reading)
    {
        var ts = new BsonDateTime(UtcTime.ToBson(reading.MeasurementTime));
        BsonValue soc = reading.SocPercent is { } s ? new BsonDouble((double)s) : BsonNull.Value;

        var candidate = reading.PowerW is { } power
            ? Reading(ts, reading.EnergyWh, soc, new BsonInt32(power), derived: false)
            : new BsonDocument("$let", new BsonDocument
            {
                { "vars", new BsonDocument("derived", Derived(ts, reading.EnergyWh)) },
                { "in", Reading(ts, reading.EnergyWh, soc, "$$derived", new BsonDocument("$ne", new BsonArray { "$$derived", BsonNull.Value })) },
            });

        var isNewer = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$lastReading"), "missing" }),
            new BsonDocument("$eq", new BsonArray { "$lastReading", BsonNull.Value }),
            new BsonDocument("$lt", new BsonArray { "$lastReading.ts", ts }),
        });

        var stage = new BsonDocument("$set", new BsonDocument(
            "lastReading",
            new BsonDocument("$cond", new BsonArray { isNewer, candidate, "$lastReading" })));

        return Builders<BsonDocument>.Update.Pipeline(new BsonDocument[] { stage });
    }

    private static BsonDocument Reading(BsonDateTime ts, long energyWh, BsonValue soc, BsonValue powerW, BsonValue derived) => new()
    {
        { "ts", ts },
        { "energyWh", energyWh },
        { "powerW", powerW },
        { "socPercent", soc },
        { "powerDerived", derived },
    };

    // (energy added) × 3 600 000 / (gap in ms) = W. Null when there is no earlier reading, the gap is
    // outside 1 s–15 min, the meter went backwards, or the result does not fit a 32-bit power. $and stops
    // at the first false operand, so the division never sees a zero gap.
    private static BsonDocument Derived(BsonDateTime ts, long energyWh)
    {
        var gapMs = new BsonDocument("$subtract", new BsonArray { ts, "$lastReading.ts" });
        var addedWh = new BsonDocument("$subtract", new BsonArray { energyWh, "$lastReading.energyWh" });
        var watts = new BsonDocument("$round", new BsonArray
        {
            new BsonDocument("$divide", new BsonArray { new BsonDocument("$multiply", new BsonArray { "$$addedWh", 3_600_000L }), "$$gapMs" }),
            0,
        });

        return new BsonDocument("$let", new BsonDocument
        {
            { "vars", new BsonDocument { { "gapMs", gapMs }, { "addedWh", addedWh } } },
            {
                "in", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$and", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$lastReading.ts"), "date" }),
                        new BsonDocument("$gte", new BsonArray { "$$gapMs", (long)MinDerivationGap.TotalMilliseconds }),
                        new BsonDocument("$lte", new BsonArray { "$$gapMs", (long)MaxDerivationGap.TotalMilliseconds }),
                        new BsonDocument("$gte", new BsonArray { "$$addedWh", 0 }),
                        new BsonDocument("$lte", new BsonArray { watts, (long)int.MaxValue }),
                    }),
                    new BsonDocument("$toInt", watts),
                    BsonNull.Value,
                })
            },
        });
    }
}
