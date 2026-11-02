using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// The whole push path with real parts: the heat-pump-cloud simulator posts signed webhooks over HTTP to the gateway
/// on Kestrel, the worker stores the readings, and the owner-scoped device reader (what <c>/v1/devices</c> uses)
/// shows the heat pump.
/// </summary>
public sealed class WebhookEndToEndTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_simulated_heat_pump_appears_in_the_device_reader_with_its_last_reading()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: HeatPumpWebhooks.Settings());
        await gateway.StartAsync();
        var worker = await WorkerHost.StartAsync(mongo, rabbit, gateway.DatabaseName, gateway.QueuePrefix, new Dictionary<string, string>());
        try
        {
            var settings = HeatPumpSimulatorHost.DefaultSettings(callbackUrl: new Uri(gateway.BaseUri, "webhooks/heatpumpcloud/acct_demo").ToString());
            settings["Simulator:Interval"] = "00:00:01";
            await using var simulator = await HeatPumpSimulatorHost.StartAsync(TimeProvider.System, settings);

            var owner = OwnerId.Parse("own_demo");
            var reader = gateway.Services.GetRequiredService<IDeviceReader>();
            var devices = await WorkerHost.EventuallyAsync(
                () => reader.ListAsync(owner, after: null, take: 50, Ct),
                list => list.Any(d => d.Type == DeviceType.HeatPump && d.State?.LastReading is not null),
                TimeSpan.FromSeconds(30));

            var heatPump = Assert.Single(devices, d => d.Type == DeviceType.HeatPump);
            Assert.Equal("heatpumpcloud", heatPump.ProviderId);
            Assert.Equal("hp_1", heatPump.ExternalId);
            Assert.Equal(DeviceStatus.Available, heatPump.State!.Status);
            Assert.Equal("AeroHeat 9", heatPump.State.Hardware?.Model);
            Assert.Equal(3_000, heatPump.State.LastReading!.PowerW);
            Assert.True(heatPump.State.LastReading.EnergyWh >= 1_000_000);
            Assert.Null(await reader.GetAsync(OwnerId.Parse("own_other"), heatPump.Id, Ct));
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
        }
    }
}
