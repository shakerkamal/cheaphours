using System.Net;
using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// The telemetry scenarios of the device-api spec through the API role: range widening, paging, owner
/// scoping, and a round trip from the worker's writer to the JSON a client sees.
/// </summary>
public sealed class TelemetryEndpointTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset Day = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ten = Day.AddHours(10);

    private readonly FakeTimeProvider _time = new(Day.AddDays(1));
    private ApiFactory _api = null!;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(mongo.ConnectionString, _time);
        await _api.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private IMongoDatabase Database => mongo.Client.GetDatabase(_api.DatabaseName);

    private async Task<DeviceId> ChargerAsync(string owner)
    {
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var device = new Device(DeviceId.New(_time), OwnerId.Parse(owner), "ocpp", $"CP-{Guid.NewGuid():N}", DeviceType.Charger, Capabilities.ReadTelemetry, new DeviceAttributes(maxPowerW: 11000));
        return await new DeviceStateStore(Database).EnsureAsync(device, TimeSpan.FromSeconds(60), _time.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    private Task StoreAsync(DeviceId device, IEnumerable<DeviceReading> readings) =>
        new TelemetryBatchWriter(Database, _time)
            .WriteAsync("worker.telemetry", [new TelemetryBatchItem(Guid.CreateVersion7(), device, "ocpp", [.. readings], IsRepeat: false)], TestContext.Current.CancellationToken);

    private static DeviceReading At(DateTimeOffset time, long energyWh, decimal? soc = null) => new("CP", time, null, energyWh, soc);

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string key, DeviceId device, string query)
    {
        using var client = _api.CreateClient(key);
        using var response = await client.GetAsync(new Uri($"/v1/devices/{device}/telemetry?{query}", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    private static string[] Starts(JsonElement page) => [.. page.GetProperty("data").EnumerateArray().Select(b => b.GetProperty("start").GetString()!)];

    [Fact]
    public async Task Range_is_widened_to_whole_slots()
    {
        var device = await ChargerAsync("own_a");
        await StoreAsync(device, Enumerable.Range(0, 7).Select(i => At(Ten.AddMinutes(-15 + (i * 15) + 5), 100 + i))); // 09:50 … 11:20

        var (status, page) = await GetAsync(ApiFactory.ReadKeyA, device, "from=2026-10-20T10:07:00Z&to=2026-10-20T10:52:00Z");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["2026-10-20T10:00:00Z", "2026-10-20T10:15:00Z", "2026-10-20T10:30:00Z", "2026-10-20T10:45:00Z"], Starts(page));
    }

    [Fact]
    public async Task A_full_day_comes_in_two_pages_without_repeats()
    {
        var device = await ChargerAsync("own_a");
        await StoreAsync(device, Enumerable.Range(0, 96).Select(i => At(Day.AddMinutes((i * 15) + 1), 1000 + (i * 10))));

        var (_, first) = await GetAsync(ApiFactory.ReadKeyA, device, "from=2026-10-20T00:00:00Z&to=2026-10-21T00:00:00Z&limit=50");
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var (_, second) = await GetAsync(ApiFactory.ReadKeyA, device, $"from=2026-10-20T00:00:00Z&to=2026-10-21T00:00:00Z&limit=50&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(50, first.GetProperty("data").GetArrayLength());
        Assert.Equal(46, second.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var all = Starts(first).Concat(Starts(second)).ToList();
        Assert.Equal(96, all.Distinct().Count());
        Assert.Equal(all.Order(StringComparer.Ordinal), all);
    }

    [Fact]
    public async Task Another_owners_device_is_not_found()
    {
        var device = await ChargerAsync("own_a");
        await StoreAsync(device, [At(Ten.AddMinutes(1), 100)]);

        var (status, problem) = await GetAsync(ApiFactory.ReadKeyB, device, "from=2026-10-20T10:00:00Z&to=2026-10-20T11:00:00Z");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("https://cheaphours.dev/errors/not-found", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Readings_without_a_device_are_not_found()
    {
        var gone = DeviceId.Parse("dev_gone");
        await StoreAsync(gone, [At(Ten.AddMinutes(1), 100)]);

        var (status, _) = await GetAsync(ApiFactory.ReadKeyA, gone, "from=2026-10-20T10:00:00Z&to=2026-10-20T11:00:00Z");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Readings_written_by_the_worker_come_back_as_buckets()
    {
        var device = await ChargerAsync("own_a");
        await StoreAsync(device, [At(Ten.AddMinutes(14), 10_000, 40m), At(Ten.AddMinutes(16), 10_300), At(Ten.AddMinutes(29), 12_750, 43.5m)]);

        var (status, page) = await GetAsync(ApiFactory.ReadKeyA, device, "from=2026-10-20T10:15:00Z&to=2026-10-20T10:30:00Z&resolution=15m");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"data":[{"start":"2026-10-20T10:15:00Z","end":"2026-10-20T10:30:00Z","avgPowerW":11000,"energyWh":2750,"socPercent":43.5}],"nextCursor":null}""",
            page.GetRawText());
    }
}
