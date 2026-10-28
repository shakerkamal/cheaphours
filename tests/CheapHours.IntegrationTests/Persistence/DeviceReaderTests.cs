using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Persistence;

public sealed class DeviceReaderTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");
    private static readonly OwnerId OwnerB = OwnerId.Parse("own_b");

    private readonly IMongoDatabase _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(DeviceReaderTests)));
    private readonly FakeTimeProvider _time = new(T0);

    private DeviceReader Reader => new(_database);

    public async ValueTask InitializeAsync() => await MongoIndexes.EnsureAsync(_database, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Each device is created a millisecond after the previous one, so ids sort in creation order.
    private async Task<DeviceId> CreateAsync(OwnerId owner, string externalId)
    {
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var device = new Device(DeviceId.New(_time), owner, "ocpp", externalId, DeviceType.Charger, Capabilities.ReadTelemetry, new DeviceAttributes(maxPowerW: 11000));
        return await new DeviceStateStore(_database).EnsureAsync(device, TimeSpan.FromSeconds(60), _time.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Own_device_is_read_with_its_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync(OwnerA, "A-GET");

        var device = await Reader.GetAsync(OwnerA, id, ct);

        Assert.NotNull(device);
        Assert.Equal(id, device.Id);
        Assert.Equal(DeviceStatus.Unknown, device.Status);
        Assert.Equal(TimeSpan.FromSeconds(60), device.State!.ReportInterval);
    }

    [Fact]
    public async Task Another_owners_device_is_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync(OwnerA, "A-HIDDEN");

        Assert.Null(await Reader.GetAsync(OwnerB, id, ct));
        Assert.DoesNotContain(await Reader.ListAsync(OwnerB, null, 200, ct), d => d.Id == id);
    }

    [Fact]
    public async Task Unknown_device_is_not_found()
    {
        Assert.Null(await Reader.GetAsync(OwnerA, DeviceId.Parse("dev_missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owner_without_devices_gets_an_empty_list()
    {
        Assert.Empty(await Reader.ListAsync(OwnerId.Parse("own_nobody"), null, 50, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_pages_in_creation_order_without_repeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = OwnerId.Parse("own_pages");
        var created = new List<DeviceId>();
        foreach (var n in Enumerable.Range(1, 5))
        {
            created.Add(await CreateAsync(owner, $"P-{n}"));
        }

        await CreateAsync(OwnerB, "B-INTERLEAVED");

        var seen = new List<DeviceId>();
        DeviceId? after = null;
        var pages = new List<int>();
        while (true)
        {
            var page = await Reader.ListAsync(owner, after, 2, ct);
            if (page.Count == 0)
            {
                break;
            }

            pages.Add(page.Count);
            seen.AddRange(page.Select(d => d.Id));
            after = page[^1].Id;
        }

        Assert.Equal([2, 2, 1], pages);
        Assert.Equal(created, seen);
    }
}
