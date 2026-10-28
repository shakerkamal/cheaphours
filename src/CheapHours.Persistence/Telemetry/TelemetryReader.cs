using CheapHours.Core;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Telemetry;

/// <summary>
/// MongoDB implementation of <see cref="ITelemetryReader"/>: one aggregation pipeline on the time-series
/// collection, so raw readings never leave the database.
/// </summary>
/// <param name="database">The database.</param>
public sealed class TelemetryReader(IMongoDatabase database) : ITelemetryReader
{
    // The longest gap an energy increment is counted across; the same window as derived power.
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    private readonly IMongoCollection<BsonDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TelemetryBucket>> GetBucketsAsync(DeviceId deviceId, DateTimeOffset startAt, DateTimeOffset endAt, int take, CancellationToken cancellationToken)
    {
        if (!SlotGrid.IsAligned(startAt) || !SlotGrid.IsAligned(endAt) || endAt <= startAt)
        {
            throw new ArgumentException($"The range must be whole 15-minute slots with start before end, but is {startAt:O}–{endAt:O}.", nameof(startAt));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        using var cursor = await _collection
            .AggregateAsync<BsonDocument>(Pipeline(deviceId, startAt, endAt, take), new AggregateOptions { AllowDiskUse = true }, cancellationToken)
            .ConfigureAwait(false);
        var rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ConvertAll(row => new TelemetryBucket(
            new DateTimeOffset(row["_id"].ToUniversalTime(), TimeSpan.Zero),
            row["energyWh"].ToInt64(),
            row["socPercent"].IsBsonNull ? null : (decimal)row["socPercent"].ToDouble()));
    }

    private static PipelineDefinition<BsonDocument, BsonDocument> Pipeline(DeviceId deviceId, DateTimeOffset start, DateTimeOffset end, int take)
    {
        var startUtc = start.UtcDateTime;

        // An increment counts only when there is a previous reading, at most 15 minutes older, whose
        // energy is not higher (a lower value is a meter reset).
        var increment = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { "$prevTs", BsonNull.Value }),
                new BsonDocument("$lte", new BsonArray { new BsonDocument("$subtract", new BsonArray { "$ts", "$prevTs" }), (long)MaxGap.TotalMilliseconds }),
                new BsonDocument("$gte", new BsonArray { "$energyWh", "$prevEnergyWh" }),
            }),
            new BsonDocument("$subtract", new BsonArray { "$energyWh", "$prevEnergyWh" }),
            0L,
        });
        var soc = new BsonDocument("$ifNull", new BsonArray { "$socPercent", BsonNull.Value });

        BsonDocument[] stages =
        [
            // The lookback brings in the reading before the first slot, so its first increment counts.
            new("$match", new BsonDocument
            {
                { "meta.deviceId", deviceId.Value },
                { "ts", new BsonDocument { { "$gte", (start - MaxGap).UtcDateTime }, { "$lt", end.UtcDateTime } } },
            }),
            new("$setWindowFields", new BsonDocument
            {
                // energyWh breaks ties between equal timestamps, so the result does not depend on storage order.
                { "sortBy", new BsonDocument { { "ts", 1 }, { "energyWh", 1 } } },
                {
                    "output", new BsonDocument
                    {
                        { "prevTs", new BsonDocument("$shift", new BsonDocument { { "output", "$ts" }, { "by", -1 } }) },
                        { "prevEnergyWh", new BsonDocument("$shift", new BsonDocument { { "output", "$energyWh" }, { "by", -1 } }) },
                    }
                },
            }),
            new("$match", new BsonDocument("ts", new BsonDocument("$gte", new BsonDateTime(startUtc)))),
            new("$set", new BsonDocument
            {
                { "increment", increment },
                { "hasSoc", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { soc, BsonNull.Value }), 0, 1 }) },
            }),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$ts" }, { "unit", "minute" }, { "binSize", 15 }, { "timezone", "UTC" } }) },
                { "energyWh", new BsonDocument("$sum", "$increment") },

                // The latest reading that reported a state of charge, else null.
                { "socPercent", new BsonDocument("$bottom", new BsonDocument { { "sortBy", new BsonDocument { { "hasSoc", 1 }, { "ts", 1 }, { "energyWh", 1 } } }, { "output", soc } }) },
            }),
            new("$sort", new BsonDocument("_id", 1)),
            new("$limit", take),
        ];

        return stages;
    }

    private static IMongoCollection<BsonDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.GetCollection<BsonDocument>(MongoIndexes.Telemetry);
    }
}
