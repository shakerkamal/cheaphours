using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// The bucket rules of the device-api spec, against readings stored by the real writer, so the reader
/// reads exactly the document shape the worker writes.
/// </summary>
public sealed class TelemetryReaderTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(TelemetryReaderTests)));

    private TelemetryReader Reader => new(_database);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DeviceId NewDevice() => DeviceId.Parse($"dev_{Guid.NewGuid():N}");

    private static DeviceReading At(int minute, long energyWh, decimal? soc = null) => new("CP001", Ten.AddMinutes(minute), null, energyWh, soc);

    private Task StoreAsync(DeviceId device, params DeviceReading[] readings) =>
        new TelemetryBatchWriter(_database, new FakeTimeProvider(Ten))
            .WriteAsync("worker.telemetry", [new TelemetryBatchItem(Guid.CreateVersion7(), device, "ocpp", readings, IsRepeat: false)], TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<TelemetryBucket>> BucketsAsync(DeviceId device, int fromMinute, int toMinute, int take = 200) =>
        Reader.GetBucketsAsync(device, Ten.AddMinutes(fromMinute), Ten.AddMinutes(toMinute), take, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Only_slots_with_readings_appear_in_order()
    {
        var device = NewDevice();
        await StoreAsync(device, At(1, 100), At(14, 110), At(46, 120));

        var buckets = await BucketsAsync(device, 0, 60);

        Assert.Equal([Ten, Ten.AddMinutes(45)], buckets.Select(b => b.Start));
        Assert.All(buckets, b => Assert.Equal(b.Start.AddMinutes(15), b.End));
    }

    [Fact]
    public async Task Increments_inside_one_bucket_add_up()
    {
        var device = NewDevice();
        await StoreAsync(device, At(0, 1000), At(5, 1500), At(10, 2000));

        var bucket = Assert.Single(await BucketsAsync(device, 0, 15));

        Assert.Equal(1000, bucket.EnergyWh);
        Assert.Equal(4000, bucket.AvgPowerW);
    }

    [Fact]
    public async Task Increment_across_a_boundary_counts_in_the_later_bucket()
    {
        var device = NewDevice();
        await StoreAsync(device, At(14, 1000), At(16, 1300));

        var bucket = Assert.Single(await BucketsAsync(device, 15, 30));

        Assert.Equal(Ten.AddMinutes(15), bucket.Start);
        Assert.Equal(300, bucket.EnergyWh);
    }

    [Fact]
    public async Task Gap_longer_than_15_minutes_counts_as_zero()
    {
        var device = NewDevice();
        await StoreAsync(device, At(0, 1000), At(20, 1800));

        var buckets = await BucketsAsync(device, 0, 30);

        Assert.Equal(0, buckets.Single(b => b.Start == Ten.AddMinutes(15)).EnergyWh);
    }

    [Fact]
    public async Task Gap_of_exactly_15_minutes_still_counts()
    {
        var device = NewDevice();
        await StoreAsync(device, At(0, 1000), At(15, 1100));

        var buckets = await BucketsAsync(device, 0, 30);

        Assert.Equal(100, buckets.Single(b => b.Start == Ten.AddMinutes(15)).EnergyWh);
    }

    [Fact]
    public async Task Meter_reset_counts_as_zero_and_counting_resumes()
    {
        var device = NewDevice();
        await StoreAsync(device, At(0, 5000), At(5, 100), At(10, 400));

        Assert.Equal(300, Assert.Single(await BucketsAsync(device, 0, 15)).EnergyWh);
    }

    [Fact]
    public async Task State_of_charge_is_the_last_one_reported_in_the_bucket()
    {
        var device = NewDevice();
        await StoreAsync(device, At(2, 100, 40m), At(5, 110), At(9, 120, 42m), At(12, 130));

        Assert.Equal(42m, Assert.Single(await BucketsAsync(device, 0, 15)).SocPercent);
    }

    [Fact]
    public async Task Bucket_without_state_of_charge_reports_it_unknown()
    {
        var device = NewDevice();
        await StoreAsync(device, At(2, 100), At(5, 110));

        Assert.Null(Assert.Single(await BucketsAsync(device, 0, 15)).SocPercent);
    }

    [Fact]
    public async Task Buckets_lie_on_the_grid_and_inside_the_range()
    {
        var device = NewDevice();
        await StoreAsync(device, [.. Enumerable.Range(0, 7).Select(i => At(-8 + (i * 15), 100 + i))]); // 09:52, 10:07, … 11:22

        var buckets = await BucketsAsync(device, 0, 60);

        Assert.Equal([Ten, Ten.AddMinutes(15), Ten.AddMinutes(30), Ten.AddMinutes(45)], buckets.Select(b => b.Start));
        Assert.All(buckets, b => Assert.Equal(1, b.EnergyWh)); // each reading is 15 minutes after the previous one
    }

    [Fact]
    public async Task Readings_of_another_device_are_excluded()
    {
        var device = NewDevice();
        var other = NewDevice();
        await StoreAsync(device, At(0, 1000), At(5, 1100));
        await StoreAsync(other, At(1, 0), At(6, 9000));

        Assert.Equal(100, Assert.Single(await BucketsAsync(device, 0, 15)).EnergyWh);
    }

    [Fact]
    public async Task Take_limits_the_result()
    {
        var device = NewDevice();
        await StoreAsync(device, At(1, 100), At(16, 110), At(31, 120));

        var buckets = await BucketsAsync(device, 0, 60, take: 2);

        Assert.Equal([Ten, Ten.AddMinutes(15)], buckets.Select(b => b.Start));
    }

    [Fact]
    public async Task Device_without_readings_has_no_buckets()
    {
        Assert.Empty(await BucketsAsync(NewDevice(), 0, 60));
    }
}
