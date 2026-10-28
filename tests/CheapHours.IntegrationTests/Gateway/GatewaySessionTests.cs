using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using CheapHours.Core.Devices;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MongoDb;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewaySessionTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0"}]""";
    private const string Heartbeat = """[2,"h1","Heartbeat",{}]""";

    private GatewayFactory _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = new GatewayFactory(mongo.ConnectionString);
        await _gateway.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    private Task<OcppTestClient> Connect(string id = "CP001", string password = GatewayFactory.Password) =>
        OcppTestClient.ConnectAsync(_gateway.OcppUri(id), id, password);

    private Task<DeviceState?> Device(string id = "CP001") => _gateway.Devices.FindAsync("ocpp", id, TestContext.Current.CancellationToken);

    private static string Status(string status, int connector = 1, string? timestamp = null) =>
        timestamp is null
            ? $$"""[2,"s-{{status}}","StatusNotification",{"connectorId":{{connector}},"errorCode":"NoError","status":"{{status}}"}]"""
            : $$"""[2,"s-{{status}}","StatusNotification",{"connectorId":{{connector}},"errorCode":"NoError","status":"{{status}}","timestamp":"{{timestamp}}"}]""";

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The condition did not become true within 10 s.");
    }

    private static void AssertCallError(JsonArray reply, string uniqueId, string code)
    {
        Assert.Equal(4, reply[0]!.GetValue<int>());
        Assert.Equal(uniqueId, reply[1]!.GetValue<string>());
        Assert.Equal(code, reply[2]!.GetValue<string>());
    }

    [Fact]
    public async Task Boot_heartbeat_and_status_bring_the_charger_online()
    {
        using var client = await Connect();

        var boot = await client.CallAsync(Boot);
        var heartbeat = await client.CallAsync(Heartbeat);
        var status = await client.CallAsync(Status("Available"));

        Assert.Equal(3, boot[0]!.GetValue<int>());
        Assert.Equal("b1", boot[1]!.GetValue<string>());
        Assert.Equal("Accepted", boot[2]!["status"]!.GetValue<string>());
        Assert.Equal(60, boot[2]!["interval"]!.GetValue<int>());
        Assert.Equal("h1", heartbeat[1]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(heartbeat[2]!["currentTime"]!.GetValue<string>(), CultureInfo.InvariantCulture, out _));
        Assert.Equal("""[3,"s-Available",{}]""", status.ToJsonString());

        var device = await Device();
        Assert.NotNull(device);
        Assert.Equal(DeviceStatus.Available, device.Status);
        Assert.Equal(DeviceType.Charger, device.Type);
        Assert.Equal(11000, device.MaxPowerW);
        Assert.StartsWith("dev_", device.Id.Value, StringComparison.Ordinal);
        Assert.Equal(new DeviceHardware("Solidstudio", "VCP", "1.0"), device.Hardware);
        Assert.NotNull(device.LastSeenAt);

        var entry = await _gateway.Registry.FindAsync("CP001", TestContext.Current.CancellationToken);
        Assert.Equal(GatewayFactory.ReplicaId, entry!.ReplicaId);
    }

    [Fact]
    public async Task Malformed_frames_get_errors_and_the_connection_stays_open()
    {
        using var client = await Connect();

        AssertCallError(await client.CallAsync("hello"), "-1", "FormationViolation");
        AssertCallError(await client.CallAsync("""[2,"abc-2","Heartbeat"]"""), "abc-2", "FormationViolation");
        AssertCallError(await client.CallAsync("""[7,"abc-3","Heartbeat",{}]"""), "abc-3", "FormationViolation");
        AssertCallError(await client.CallAsync("""[2,"f1","FlyToMoon",{}]"""), "f1", "NotImplemented");
        AssertCallError(await client.CallAsync("""[2,"a1","Authorize",{"idTag":"ANY"}]"""), "a1", "NotSupported");
        AssertCallError(await client.CallAsync("""[2,"m1","MeterValues",{"connectorId":1,"meterValue":[]}]"""), "m1", "PropertyConstraintViolation");
        AssertCallError(await client.CallAsync("""[2,"b2","BootNotification",{"chargePointVendor":"V"}]"""), "b2", "OccurenceConstraintViolation");

        Assert.Equal("h1", (await client.CallAsync(Heartbeat))[1]!.GetValue<string>());
        Assert.Null((await Device())!.Hardware);
    }

    [Fact]
    public async Task Unsolicited_result_gets_no_answer()
    {
        using var client = await Connect();

        await client.SendAsync("""[3,"nobody-asked",{}]""");
        var next = await client.CallAsync(Heartbeat);

        Assert.Equal("h1", next[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_status_is_refused_and_changes_nothing()
    {
        using var client = await Connect();
        await client.CallAsync(Status("Charging"));

        AssertCallError(await client.CallAsync(Status("Sleeping")), "s-Sleeping", "PropertyConstraintViolation");

        Assert.Equal(DeviceStatus.Charging, (await Device())!.Status);
    }

    [Fact]
    public async Task Binary_frame_closes_with_1003_and_others_stay_connected()
    {
        using var other = await Connect("CP002", GatewayFactory.OtherPassword);
        using var client = await Connect();

        await client.Socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, await client.WaitForCloseAsync());
        Assert.Equal("h1", (await other.CallAsync(Heartbeat))[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Oversized_frame_closes_with_1009()
    {
        using var client = await Connect();

        await client.SendAsync("[2,\"big\",\"DataTransfer\",{\"data\":\"" + new string('x', 100 * 1024) + "\"}]");

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Client_close_marks_the_charger_offline_and_clears_the_registry()
    {
        using (var client = await Connect())
        {
            await client.CallAsync(Status("Available"));
            await client.CloseAsync();
        }

        await Eventually(async () => (await Device())!.Status == DeviceStatus.Offline);
        Assert.Null(await _gateway.Registry.FindAsync("CP001", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Newer_connection_replaces_the_older_one()
    {
        using var first = await Connect();
        await first.CallAsync(Status("Available"));

        using var second = await Connect();

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await first.WaitForCloseAsync());
        Assert.Equal("h1", (await second.CallAsync(Heartbeat))[1]!.GetValue<string>());
        Assert.Equal(DeviceStatus.Available, (await Device())!.Status);
        Assert.NotNull(await _gateway.Registry.FindAsync("CP001", TestContext.Current.CancellationToken));
        var devices = mongo.Client.GetDatabase(_gateway.DatabaseName).GetCollection<MongoDB.Bson.BsonDocument>(CheapHours.Persistence.MongoIndexes.Devices);
        Assert.Equal(1, await devices.CountDocumentsAsync(new MongoDB.Bson.BsonDocument("externalId", "CP001"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_recorded_just_before_a_reconnect_is_applied()
    {
        using (var client = await Connect())
        {
            await client.CloseAsync();
        }

        await Eventually(async () => (await Device())!.Status == DeviceStatus.Offline);
        using var again = await Connect();

        await again.CallAsync(Status("Charging", timestamp: DateTimeOffset.UtcNow.AddSeconds(-2).ToString("O", CultureInfo.InvariantCulture)));

        Assert.Equal(DeviceStatus.Charging, (await Device())!.Status);
    }

    [Fact]
    public async Task Reconnecting_charger_is_unknown_until_it_reports_a_status()
    {
        using (var client = await Connect())
        {
            await client.CloseAsync();
        }

        await Eventually(async () => (await Device())!.Status == DeviceStatus.Offline);
        var firstId = (await Device())!.Id;

        using var again = await Connect();
        await again.CallAsync(Heartbeat);
        Assert.Equal(DeviceStatus.Unknown, (await Device())!.Status);

        await again.CallAsync(Status("Available"));
        var device = await Device();
        Assert.Equal(DeviceStatus.Available, device!.Status);
        Assert.Equal(firstId, device.Id);
    }

    [Fact]
    public async Task Late_notification_does_not_overwrite_newer_state()
    {
        using var client = await Connect();
        var now = DateTimeOffset.UtcNow;
        await client.CallAsync(Status("Charging", timestamp: now.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture)));

        var reply = await client.CallAsync(Status("Available", timestamp: now.AddMinutes(-6).ToString("O", CultureInfo.InvariantCulture)));

        Assert.Equal(3, reply[0]!.GetValue<int>());
        Assert.Equal(DeviceStatus.Charging, (await Device())!.Status);
    }

    [Fact]
    public async Task Future_timestamp_is_recorded_at_receipt()
    {
        using var client = await Connect();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await client.CallAsync(Status("Faulted", timestamp: DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture)));

        var device = await Device();
        Assert.Equal(DeviceStatus.Faulted, device!.Status);
        Assert.InRange(device.StatusAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Second_connector_is_acknowledged_without_change()
    {
        using var client = await Connect();
        await client.CallAsync(Status("Available"));

        Assert.Equal("""[3,"s-Faulted",{}]""", (await client.CallAsync(Status("Faulted", connector: 2))).ToJsonString());
        Assert.Equal(DeviceStatus.Available, (await Device())!.Status);
    }

    [Fact]
    public async Task Every_frame_moves_last_seen_forward()
    {
        using var client = await Connect();
        await client.CallAsync(Status("Available"));
        var first = (await Device())!.LastSeenAt;

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await client.CallAsync("not even json");

        Assert.True((await Device())!.LastSeenAt > first);
    }

    [Fact]
    public async Task Database_failure_during_boot_is_an_internal_error_without_details()
    {
        await using var container = new MongoDbBuilder(MongoFixture.Image).WithReplicaSet().Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        await using var gateway = new GatewayFactory(container.GetConnectionString(), overrides: new Dictionary<string, string?>
        {
            ["Mongo:ServerSelectionTimeout"] = "00:00:01",
        });
        await gateway.StartAsync();
        using var client = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);

        await container.StopAsync(TestContext.Current.CancellationToken);
        var reply = await client.CallAsync(Boot);

        AssertCallError(reply, "b1", "InternalError");
        var description = reply[3]!.GetValue<string>();
        Assert.DoesNotContain("mongodb", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", description, StringComparison.Ordinal);
        Assert.Equal("h1", (await client.CallAsync(Heartbeat))[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Silent_charger_is_closed_marked_offline_and_removed_from_the_registry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var gateway = new GatewayFactory(mongo.ConnectionString, time);
        await gateway.StartAsync();
        using var client = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await client.CallAsync(Status("Available"));

        // Advance the fake clock one check period (15 s) at a time; the monitor runs on it.
        var elapsed = TimeSpan.Zero;
        while (client.Socket.State == WebSocketState.Open && elapsed < TimeSpan.FromMinutes(10))
        {
            time.Advance(TimeSpan.FromSeconds(15));
            elapsed += TimeSpan.FromSeconds(15);
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        Assert.True(elapsed > TimeSpan.FromSeconds(180), $"Closed after only {elapsed} of silence.");
        await Eventually(async () => (await gateway.Devices.FindAsync("ocpp", "CP001", TestContext.Current.CancellationToken))!.Status == DeviceStatus.Offline);
        Assert.Null(await gateway.Registry.FindAsync("CP001", TestContext.Current.CancellationToken));
    }
}
