using System.Net;
using System.Net.WebSockets;
using CheapHours.Core.Devices;
using CheapHours.Host.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewayShutdownTests(MongoFixture mongo)
{
    private const string Available = """[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Available"}]""";

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

    [Fact]
    public async Task Stop_closes_every_connection_with_1001_and_clears_the_registry_without_marking_offline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();

        // The host disposes its services once it stops, so check MongoDB through stores of our own.
        var database = mongo.Client.GetDatabase(gateway.DatabaseName);
        var registry = new ConnectionRegistry(database);
        var devices = new DeviceStateStore(database);

        using var a = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        using var b = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP002"), "CP002", GatewayFactory.OtherPassword);
        await a.CallAsync(Available);
        await b.CallAsync(Available);

        gateway.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await a.WaitForCloseAsync());
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await b.WaitForCloseAsync());
        await Eventually(async () => await registry.FindAsync("CP001", ct) is null && await registry.FindAsync("CP002", ct) is null);
        Assert.Equal(DeviceStatus.Available, (await devices.FindAsync("ocpp", "CP001", ct))!.Status);
        Assert.Equal(DeviceStatus.Available, (await devices.FindAsync("ocpp", "CP002", ct))!.Status);
    }

    [Fact]
    public async Task While_draining_upgrades_get_503_and_readiness_fails()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var client = gateway.CreateClient();

        // The state the stop signal sets first; the listener is still open, as for a request already in flight.
        gateway.Services.GetRequiredService<GatewayState>().BeginDraining();

        var (status, _, _) = await OcppTestClient.UpgradeAsync(client, "/ocpp/CP001", OcppTestClient.Basic("CP001", GatewayFactory.Password));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task After_stop_new_chargers_are_turned_away()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        var uri = gateway.OcppUri("CP001");

        gateway.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Either the request reached the draining gateway (503) or the listener was already closed.
        await Assert.ThrowsAsync<WebSocketException>(() => OcppTestClient.ConnectAsync(uri, "CP001", GatewayFactory.Password));
    }
}
