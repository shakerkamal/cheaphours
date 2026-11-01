using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>Every poll scenario of the <c>car-cloud-provider</c> spec, against the WireMock vendor.</summary>
public sealed class CarCloudPollTests
{
    private static readonly DateTimeOffset At1800 = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DeviceEvent> Events(PollResult result) => Assert.IsType<PollResult.Completed>(result).Events;

    [Fact]
    public async Task One_vehicle_charging()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1", model: "Model E", capacityKwh: 77m);
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z", chargingState: "Charging", soc: 42.5m, powerKw: 11m, sessionKwh: 3.25m);

        var events = Events(await run.PollAsync());

        Assert.Collection(
            events,
            e => Assert.Equal(new DeviceEvent.Discovered("veh_1", At1800, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 77_000)), e),
            e => Assert.Equal("Model E", Assert.IsType<DeviceEvent.Connected>(e).Hardware!.Model),
            e => Assert.Equal(new DeviceEvent.StatusChanged("veh_1", At1800, DeviceStatus.Charging), e),
            e => Assert.Equal(new DeviceReading("veh_1", At1800, 11_000, 3_250, 42.5m), Assert.IsType<DeviceEvent.ReadingReported>(e).Reading));
    }

    [Fact]
    public async Task Account_without_vehicles()
    {
        await using var run = await CarCloudRun.StartAsync();

        Assert.Empty(Events(await run.PollAsync()));
    }

    [Theory]
    [InlineData("Stopped")]
    [InlineData("Complete")]
    [InlineData("Unplugged")]
    public async Task Plugged_in_or_done_but_not_charging_is_available(string chargingState)
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:05:00Z", chargingState: chargingState, powerKw: null);

        var status = Assert.Single(Events(await run.PollAsync()).OfType<DeviceEvent.StatusChanged>());

        Assert.Equal(new DeviceEvent.StatusChanged("veh_1", At1800.AddMinutes(5), DeviceStatus.Available), status);
    }

    [Fact]
    public async Task Vehicle_asleep_is_offline_without_a_reading()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z", online: false);

        var events = Events(await run.PollAsync());

        Assert.Equal(DeviceStatus.Offline, Assert.Single(events.OfType<DeviceEvent.StatusChanged>()).Status);
        Assert.Empty(events.OfType<DeviceEvent.ReadingReported>());
    }

    [Fact]
    public async Task New_session_after_a_finished_one_restarts_the_meter()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z", sessionKwh: 12.5m);
        var first = Events(await run.PollAsync()).OfType<DeviceEvent.ReadingReported>().Single().Reading;
        run.Vendor.SetState("veh_1", "2026-10-28T19:00:00Z", sessionKwh: 0.4m);
        var second = Events(await run.PollAsync()).OfType<DeviceEvent.ReadingReported>().Single().Reading;

        Assert.Equal(12_500, first.EnergyWh);
        Assert.Equal(400, second.EnergyWh);
    }

    [Fact]
    public async Task Missing_power_is_unknown_not_zero()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z", powerKw: null);

        var reading = Events(await run.PollAsync()).OfType<DeviceEvent.ReadingReported>().Single().Reading;

        Assert.Null(reading.PowerW);
    }

    [Fact]
    public async Task A_timestamp_without_offset_is_malformed_for_that_vehicle_only()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z");
        run.Vendor.AddVehicle("veh_2");
        run.Vendor.SetState("veh_2", "2026-10-28T18:00:00");

        var events = Events(await run.PollAsync());

        var malformed = Assert.Single(events.OfType<DeviceEvent.MalformedInput>());
        Assert.Contains("veh_2", malformed.Reason, StringComparison.Ordinal);
        Assert.Equal(4, events.Count(e => e is not DeviceEvent.MalformedInput));
        Assert.Contains(events, e => e is DeviceEvent.ReadingReported r && r.Reading.ExternalId == "veh_1");
        Assert.DoesNotContain(events, e => e is DeviceEvent.Discovered d && d.ExternalId == "veh_2");
    }

    [Fact]
    public async Task Vehicle_deleted_between_list_and_read_is_reported_removed()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z");
        run.Vendor.AddVehicle("veh_2");
        run.Vendor.Remove("veh_2");

        var events = Events(await run.PollAsync());

        Assert.Contains(events, e => e is DeviceEvent.DeviceRemoved { ExternalId: "veh_2" });
        Assert.Contains(events, e => e is DeviceEvent.ReadingReported r && r.Reading.ExternalId == "veh_1");
    }

    [Fact]
    public async Task Rate_limited_while_listing_fails_the_poll_and_reads_no_state()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.Server.FailWith(429, TimeSpan.FromSeconds(30));

        var failed = Assert.IsType<PollResult.Failed>(await run.PollAsync());

        Assert.Equal(TimeSpan.FromSeconds(30), failed.RetryAfter);
        Assert.DoesNotContain(run.Vendor.Server.VendorRequests, r => r.Path.EndsWith("/state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_vehicle_state_failing_leaves_only_that_vehicle_out()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z");
        run.Vendor.AddVehicle("veh_2");
        run.Vendor.FailState("veh_2", 503);

        var events = Events(await run.PollAsync());

        Assert.All(events, e => Assert.DoesNotContain("veh_2", e.ToString(), StringComparison.Ordinal));
        Assert.Contains(events, e => e is DeviceEvent.ReadingReported r && r.Reading.ExternalId == "veh_1");
        Assert.True(run.Logs.Contains("veh_2"));
    }

    [Fact]
    public async Task Every_vendor_request_carries_the_bearer_token()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.SetState("veh_1", "2026-10-28T18:00:00Z");

        Events(await run.PollAsync());

        var issued = run.Vendor.Server.AllSecrets().Where(s => s.StartsWith("access-", StringComparison.Ordinal)).ToList();
        var token = Assert.Single(issued);
        Assert.All(run.Vendor.Server.VendorRequests, r => Assert.Equal($"Bearer {token}", r.Header("Authorization")));
    }

    [Fact]
    public async Task Secrets_are_not_logged_when_the_vendor_echoes_the_request()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        var echo = $$"""{"error":"boom","request":"client_secret={{run.Vendor.Server.ClientSecret}}&refresh_token={{run.Vendor.Server.SeedRefreshToken}}"}""";
        run.Vendor.Server.FailWith(500, body: echo);

        var failed = Assert.IsType<PollResult.Failed>(await run.PollAsync());

        foreach (var secret in run.Vendor.Server.AllSecrets())
        {
            Assert.DoesNotContain(secret, failed.Reason, StringComparison.Ordinal);
            Assert.False(run.Logs.Contains(secret), $"A log line contains a secret:\n{string.Join('\n', run.Logs.Lines)}");
        }
    }
}
