using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>Start, stop and refusals of the <c>car-cloud-provider</c> spec, against the WireMock vendor.</summary>
public sealed class CarCloudCommandTests
{
    private const string Key = "plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging";

    private static DeviceCommand Start(string key = Key) =>
        new(CommandId.Parse("cmd_1"), DeviceId.Parse("dev_veh1"), CommandType.StartCharging, key);

    private static Task<SendResult> SendAsync(CarCloudRun run, DeviceCommand command, string vehicle = "veh_1") =>
        run.Provider.SendAsync(command, new DeviceAddress(vehicle, run.Account), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Start_accepted_carries_the_key_and_returns_the_vendor_request_id()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");

        var result = await SendAsync(run, Start());

        var accepted = Assert.IsType<SendResult.Accepted>(result);
        Assert.StartsWith("req-", accepted.VendorReference, StringComparison.Ordinal);
        var request = Assert.Single(run.Vendor.Server.VendorRequests, r => r.Method == "POST");
        Assert.Equal("/api/v1/vehicles/veh_1/charging/start", request.Path);
        Assert.Equal(Key, request.Header("Idempotency-Key"));
        Assert.Equal(1, run.Vendor.StartsActed("veh_1"));
    }

    [Fact]
    public async Task A_resend_with_the_same_key_is_acted_on_once()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");

        var first = Assert.IsType<SendResult.Accepted>(await SendAsync(run, Start()));
        var second = Assert.IsType<SendResult.Accepted>(await SendAsync(run, Start()));

        Assert.Equal(first.VendorReference, second.VendorReference);
        Assert.All(run.Vendor.Server.VendorRequests.Where(r => r.Method == "POST"), r => Assert.Equal(Key, r.Header("Idempotency-Key")));
        Assert.Equal(1, run.Vendor.StartsActed("veh_1"));
    }

    [Fact]
    public async Task Stop_goes_to_the_stop_endpoint()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");

        var result = await SendAsync(run, new DeviceCommand(CommandId.Parse("cmd_2"), DeviceId.Parse("dev_veh1"), CommandType.StopCharging, "k-stop"));

        Assert.IsType<SendResult.Accepted>(result);
        Assert.Equal("/api/v1/vehicles/veh_1/charging/stop", Assert.Single(run.Vendor.Server.VendorRequests, r => r.Method == "POST").Path);
    }

    [Fact]
    public async Task A_car_not_plugged_in_is_rejected_with_the_vendor_reason()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");
        run.Vendor.Unplug("veh_1");

        var rejected = Assert.IsType<SendResult.Rejected>(await SendAsync(run, Start()));

        Assert.Contains("not_plugged_in", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_rejected_as_unknown_device()
    {
        await using var run = await CarCloudRun.StartAsync();

        Assert.Equal(new SendResult.Rejected("unknown-device"), await SendAsync(run, Start(), "veh_9"));
    }

    [Fact]
    public async Task A_power_limit_is_refused_before_any_call()
    {
        await using var run = await CarCloudRun.StartAsync();
        run.Vendor.AddVehicle("veh_1");

        var result = await SendAsync(run, new DeviceCommand(CommandId.Parse("cmd_3"), DeviceId.Parse("dev_veh1"), CommandType.SetPowerLimit, "k-limit", 3700));

        Assert.Equal(new SendResult.Rejected("capability-not-supported"), result);
        Assert.Empty(run.Vendor.Server.VendorRequests);
        Assert.Empty(run.Vendor.Server.TokenRequests);
    }
}
