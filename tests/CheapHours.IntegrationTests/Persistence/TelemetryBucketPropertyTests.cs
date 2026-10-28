using CheapHours.Core;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// Model-based check of the bucket pipeline: random reading sequences must give the same buckets from
/// MongoDB as from <see cref="TelemetryBucketOracle"/>, a plain C# version of the spec's rules. On a
/// failure FsCheck prints the seed to replay.
/// </summary>
public sealed class TelemetryBucketPropertyTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(TelemetryBucketPropertyTests)));

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Every three bytes make one reading: a gap (equal timestamps, short gaps and gaps over 15 minutes),
    // an energy step or a meter reset, and an optional state of charge.
    [Property(MaxTest = 25)]
    public async Task Pipeline_matches_the_reference(byte[] raw)
    {
        var readings = Readings(raw ?? []);
        var device = DeviceId.Parse($"dev_{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;
        if (readings.Count > 0)
        {
            await new TelemetryBatchWriter(_database, new FakeTimeProvider(Origin))
                .WriteAsync("worker.telemetry", [new TelemetryBatchItem(Guid.CreateVersion7(), device, "ocpp", readings, IsRepeat: false)], ct);
        }

        var end = SlotGrid.Ceiling(readings.Count > 0 ? readings[^1].MeasurementTime.AddSeconds(1) : Origin.AddHours(1));
        var reader = new TelemetryReader(_database);

        // The whole range, and one starting later, which needs the lookback for its first increment.
        foreach (var start in new[] { Origin, Origin.AddMinutes(45) })
        {
            if (start >= end)
            {
                continue;
            }

            var expected = TelemetryBucketOracle.Buckets(readings, start, end);
            var actual = await reader.GetBucketsAsync(device, start, end, 1000, ct);

            Assert.Equal(expected, actual);
        }
    }

    private static List<DeviceReading> Readings(byte[] raw)
    {
        var readings = new List<DeviceReading>();
        var at = Origin.AddMinutes(-10);
        var energy = 1_000L;
        for (var i = 0; i + 2 < raw.Length; i += 3)
        {
            var (gap, step, flags) = (raw[i], raw[i + 1], raw[i + 2]);
            var sameTime = gap < 20 && readings.Count > 0;
            at = sameTime ? at : at.AddSeconds(gap * 5);

            // Resets never share a timestamp, so (time, energy) stays unique and the order is well defined.
            energy = !sameTime && flags % 13 == 0 ? step : energy + 1 + (step * 3);
            decimal? soc = flags % 4 == 0 ? flags % 101 : null;
            readings.Add(new DeviceReading("CP001", at, null, energy, soc));
        }

        return readings;
    }
}

/// <summary>The device-api bucket rules in plain C#, used only as the oracle for the pipeline.</summary>
internal static class TelemetryBucketOracle
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    public static List<TelemetryBucket> Buckets(IEnumerable<DeviceReading> readings, DateTimeOffset start, DateTimeOffset end)
    {
        var ordered = readings.OrderBy(r => r.MeasurementTime).ThenBy(r => r.EnergyWh).ToList();
        var rows = ordered.Select((r, i) =>
        {
            var increment = 0L;
            if (i > 0)
            {
                var prev = ordered[i - 1];
                if (r.MeasurementTime - prev.MeasurementTime <= MaxGap && r.EnergyWh >= prev.EnergyWh)
                {
                    increment = r.EnergyWh - prev.EnergyWh;
                }
            }

            return (Reading: r, Increment: increment);
        });

        return rows
            .Where(x => x.Reading.MeasurementTime >= start && x.Reading.MeasurementTime < end)
            .GroupBy(x => SlotGrid.Floor(x.Reading.MeasurementTime))
            .OrderBy(g => g.Key)
            .Select(g => new TelemetryBucket(
                g.Key,
                g.Sum(x => x.Increment),
                g.LastOrDefault(x => x.Reading.SocPercent is not null).Reading?.SocPercent))
            .ToList();
    }
}
