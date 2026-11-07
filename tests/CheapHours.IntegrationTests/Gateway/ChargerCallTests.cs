using System.Text.Json.Nodes;
using CheapHours.Host.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Providers.Ocpp.Framing;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// Calls the gateway sends to a test charger on a real WebSocket (ocpp-gateway spec: the charger's answer becomes
/// the command result; calls that get no answer are transient).
/// </summary>
public sealed class ChargerCallTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";

    private GatewayFactory _gateway = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _gateway = new GatewayFactory(mongo.ConnectionString);
        await _gateway.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Charger_reply_with_the_calls_unique_id_completes_the_call()
    {
        using var charger = await ConnectAsync();
        var connection = Connection();

        var call = connection.CallAsync("RemoteStartTransaction", new JsonObject { ["connectorId"] = 1, ["idTag"] = "CHEAPHOURS" }, TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        var frame = await charger.ReceiveAsync();

        // A stray result for another call is ignored; the matching one completes the call.
        await charger.SendAsync("""[3,"not-ours",{"status":"Rejected"}]""");
        await charger.SendAsync($$"""[3,"{{frame![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");
        var outcome = await call;

        Assert.Equal(2, frame[0]!.GetValue<int>());
        Assert.Equal("RemoteStartTransaction", frame[2]!.GetValue<string>());
        Assert.Equal("""{"connectorId":1,"idTag":"CHEAPHOURS"}""", frame[3]!.ToJsonString());
        var replied = Assert.IsType<ChargerCallOutcome.Replied>(outcome);
        Assert.Equal(frame[1]!.GetValue<string>(), replied.UniqueId);
        Assert.Equal("Accepted", Assert.IsType<OcppCallResult>(replied.Reply).Payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Call_error_completes_the_call()
    {
        using var charger = await ConnectAsync();

        var call = Connection().CallAsync("RemoteStopTransaction", new JsonObject { ["transactionId"] = 1 }, TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        var frame = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[4,"{{frame![1]!.GetValue<string>()}}","NotSupported","no",{}]""");

        var replied = Assert.IsType<ChargerCallOutcome.Replied>(await call);
        Assert.Equal("NotSupported", Assert.IsType<OcppCallError>(replied.Reply).ErrorCode);
    }

    // ocpp-gateway: "Charger silent" (the waiting half).
    [Fact]
    public async Task No_reply_within_the_timeout_times_out_and_the_connection_stays_open()
    {
        using var charger = await ConnectAsync();

        var outcome = await Connection().CallAsync("RemoteStopTransaction", new JsonObject { ["transactionId"] = 1 }, TimeSpan.FromMilliseconds(500), TimeProvider.System, Ct);

        Assert.IsType<ChargerCallOutcome.TimedOut>(outcome);
        Assert.NotNull(await charger.ReceiveAsync());
        var heartbeat = await charger.CallAsync("""[2,"h1","Heartbeat",{}]""");
        Assert.Equal("h1", heartbeat[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Socket_closed_while_waiting_fails_the_call_as_closed()
    {
        var charger = await ConnectAsync();
        var call = Connection().CallAsync("RemoteStartTransaction", new JsonObject { ["connectorId"] = 1, ["idTag"] = "X" }, TimeSpan.FromSeconds(20), TimeProvider.System, Ct);
        await charger.ReceiveAsync();

        charger.Socket.Abort();
        charger.Dispose();

        Assert.IsType<ChargerCallOutcome.Closed>(await call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    private async Task<OcppTestClient> ConnectAsync()
    {
        var charger = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        return charger;
    }

    private ChargerConnection Connection() =>
        _gateway.Services.GetRequiredService<ConnectionTable>().Find("CP001") ?? throw new InvalidOperationException("CP001 is not connected.");
}
