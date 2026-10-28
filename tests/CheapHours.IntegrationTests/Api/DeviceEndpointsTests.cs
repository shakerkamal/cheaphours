using System.Net;
using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Telemetry;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// The device-list, device-by-id, device-view and silent-device scenarios of the device-api spec, against
/// the API role on real MongoDB. Devices and readings are written with the gateway's and worker's own
/// stores, so the API reads exactly what they write.
/// </summary>
public sealed class DeviceEndpointsTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");
    private static readonly OwnerId OwnerB = OwnerId.Parse("own_b");

    private readonly FakeTimeProvider _time = new(Ten);

    private async Task<ApiFactory> StartAsync()
    {
        var api = new ApiFactory(mongo.ConnectionString, _time);
        await api.StartAsync();
        return api;
    }

    private IMongoDatabase Database(ApiFactory api) => mongo.Client.GetDatabase(api.DatabaseName);

    // Each device is created a millisecond after the previous one, so ids sort in creation order.
    private async Task<DeviceId> ChargerAsync(ApiFactory api, OwnerId owner, string externalId, Capabilities capabilities = Capabilities.ReadTelemetry | Capabilities.StartStop)
    {
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var device = new Device(DeviceId.New(_time), owner, "ocpp", externalId, DeviceType.Charger, capabilities, new DeviceAttributes(maxPowerW: 11000));
        return await new DeviceStateStore(Database(api)).EnsureAsync(device, TimeSpan.FromSeconds(60), _time.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    private Task StoreReadingAsync(ApiFactory api, DeviceId device, DeviceReading reading) =>
        new TelemetryBatchWriter(Database(api), _time)
            .WriteAsync("worker.telemetry", [new TelemetryBatchItem(Guid.CreateVersion7(), device, "ocpp", [reading], IsRepeat: false)], TestContext.Current.CancellationToken);

    private Task<UpdateResult> SetAsync(ApiFactory api, DeviceId device, BsonDocument set, BsonDocument? unset = null)
    {
        var update = new BsonDocument("$set", set);
        if (unset is not null)
        {
            update.Add("$unset", unset);
        }

        return Database(api).GetCollection<BsonDocument>(MongoIndexes.Devices)
            .UpdateOneAsync(new BsonDocument("_id", device.Value), update, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(ApiFactory api, string key, string path)
    {
        using var client = api.CreateClient(key);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    private static string[] Ids(JsonElement page) => [.. page.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetString()!)];

    [Fact]
    public async Task List_holds_only_the_owners_devices_in_creation_order()
    {
        await using var api = await StartAsync();
        var first = await ChargerAsync(api, OwnerA, "A1");
        var other = await ChargerAsync(api, OwnerB, "B1");
        var second = await ChargerAsync(api, OwnerA, "A2");

        var (status, page) = await GetAsync(api, ApiFactory.ReadKeyA, "/v1/devices");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([first.Value, second.Value], Ids(page));
        Assert.DoesNotContain(other.Value, Ids(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Owner_without_devices_gets_an_empty_page()
    {
        await using var api = await StartAsync();
        await ChargerAsync(api, OwnerA, "A1");

        var (status, page) = await GetAsync(api, ApiFactory.ReadKeyB, "/v1/devices");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"data":[],"nextCursor":null}""", page.GetRawText());
    }

    [Fact]
    public async Task Following_the_cursor_yields_every_device_once()
    {
        await using var api = await StartAsync();
        var created = new List<string>();
        foreach (var n in Enumerable.Range(1, 5))
        {
            created.Add((await ChargerAsync(api, OwnerA, $"P{n}")).Value);
        }

        var seen = new List<string>();
        var sizes = new List<int>();
        var path = "/v1/devices?limit=2";
        while (true)
        {
            var (status, page) = await GetAsync(api, ApiFactory.ReadKeyA, path);
            Assert.Equal(HttpStatusCode.OK, status);
            sizes.Add(page.GetProperty("data").GetArrayLength());
            seen.AddRange(Ids(page));
            if (page.GetProperty("nextCursor").GetString() is not { } next)
            {
                break;
            }

            path = $"/v1/devices?limit=2&cursor={Uri.EscapeDataString(next)}";
        }

        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal(created, seen);
    }

    [Fact]
    public async Task Own_device_is_returned()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");

        var (status, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id.Value, device.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Another_owners_device_looks_exactly_like_a_missing_one()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");

        var (otherStatus, other) = await GetAsync(api, ApiFactory.ReadKeyB, $"/v1/devices/{id}");
        var (missingStatus, missing) = await GetAsync(api, ApiFactory.ReadKeyB, "/v1/devices/dev_does_not_exist");

        Assert.Equal(HttpStatusCode.NotFound, otherStatus);
        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal("https://cheaphours.dev/errors/not-found", other.GetProperty("type").GetString());
        foreach (var field in new[] { "type", "title", "status", "detail" })
        {
            Assert.Equal(missing.GetProperty(field).GetRawText(), other.GetProperty(field).GetRawText());
        }
    }

    [Fact]
    public async Task Charger_with_an_energy_only_reading_shows_unknown_values_as_null()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");
        await StoreReadingAsync(api, id, new DeviceReading("A1", Ten.AddSeconds(15), null, 12_500));

        var (_, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");

        Assert.Equal("ocpp", device.GetProperty("providerId").GetString());
        Assert.Equal("Charger", device.GetProperty("type").GetString());
        Assert.Equal(11000, device.GetProperty("maxPowerW").GetInt32());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("batteryCapacityWh").ValueKind);
        Assert.Equal("""["ReadTelemetry","StartStop"]""", device.GetProperty("capabilities").GetRawText());
        Assert.Equal("""{"ts":"2026-10-20T10:00:15Z","powerW":null,"energyWh":12500,"socPercent":null}""", device.GetProperty("lastReading").GetRawText());
    }

    [Fact]
    public async Task Derived_power_is_shown_as_power()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");
        await StoreReadingAsync(api, id, new DeviceReading("A1", Ten, null, 1000));
        await StoreReadingAsync(api, id, new DeviceReading("A1", Ten.AddSeconds(30), null, 1100));

        var (_, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");

        Assert.Equal(12000, device.GetProperty("lastReading").GetProperty("powerW").GetInt32());
    }

    [Fact]
    public async Task Device_without_a_reading_has_a_null_last_reading()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");

        var (_, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");

        Assert.Equal(JsonValueKind.Null, device.GetProperty("lastReading").ValueKind);
    }

    [Theory]
    [InlineData(240, "Offline")] // four minutes of silence: the gateway replica that held it is gone
    [InlineData(180, "Charging")] // exactly three intervals
    public async Task Silent_charger_is_shown_offline_without_changing_what_is_stored(int secondsSilent, string shown)
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");
        await SetAsync(api, id, new BsonDocument { { "status", "Charging" }, { "statusAt", Ten.UtcDateTime }, { "lastSeenAt", Ten.UtcDateTime } });
        _time.SetUtcNow(Ten.AddSeconds(secondsSilent));

        var (_, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");
        var (_, list) = await GetAsync(api, ApiFactory.ReadKeyA, "/v1/devices");

        Assert.Equal(shown, device.GetProperty("status").GetString());
        Assert.Equal(shown, list.GetProperty("data")[0].GetProperty("status").GetString());
        Assert.Equal("2026-10-20T10:00:00Z", device.GetProperty("lastSeenAt").GetString());
        var stored = await new DeviceStateStore(Database(api)).FindAsync("ocpp", "A1", TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatus.Charging, stored!.Status);
    }

    [Fact]
    public async Task Device_without_a_report_interval_is_shown_as_stored()
    {
        await using var api = await StartAsync();
        var id = await ChargerAsync(api, OwnerA, "A1");
        await SetAsync(
            api,
            id,
            new BsonDocument { { "status", "Available" }, { "statusAt", Ten.UtcDateTime }, { "lastSeenAt", Ten.UtcDateTime } },
            new BsonDocument("reportIntervalSeconds", ""));
        _time.SetUtcNow(Ten.AddDays(1));

        var (_, device) = await GetAsync(api, ApiFactory.ReadKeyA, $"/v1/devices/{id}");

        Assert.Equal("Available", device.GetProperty("status").GetString());
    }
}
