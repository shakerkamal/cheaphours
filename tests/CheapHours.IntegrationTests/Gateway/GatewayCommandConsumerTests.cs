using CheapHours.Core.Commands;
using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// The replica's command consumer with a test charger on a real WebSocket (ocpp-gateway spec: each replica consumes
/// its own command queue; commands become OCPP calls; a repeated send does not act twice).
/// </summary>
public sealed class GatewayCommandConsumerTests(MongoFixture mongo) : IAsyncLifetime
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";

    private GatewayFactory _gateway = null!;
    private GatewayCommands _commands = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = new GatewayFactory(mongo.ConnectionString);
        await _gateway.StartAsync();
        _commands = await GatewayCommands.StartAsync(_gateway);
    }

    public async ValueTask DisposeAsync()
    {
        await _commands.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    // ocpp-gateway: "Replica starts".
    [Fact]
    public async Task Ready_replica_consumes_its_own_queue()
    {
        Assert.Equal($"{_gateway.QueuePrefix}gateway.{GatewayFactory.ReplicaId}.commands", _commands.Queue);
        Assert.Equal(1u, await _commands.ConsumerCountAsync());
    }

    // ocpp-gateway: "Remote start" and "Charger accepts".
    [Fact]
    public async Task Remote_start_reaches_the_charger_and_an_accepted_result_is_published()
    {
        using var charger = await ConnectAsync();
        var command = GatewayCommands.Command(CommandType.StartCharging);

        await _commands.SendAsync(command);
        var call = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");

        Assert.Equal("RemoteStartTransaction", call[2]!.GetValue<string>());
        Assert.Equal("""{"connectorId":1,"idTag":"CHEAPHOURS"}""", call[3]!.ToJsonString());
        var accepted = Assert.IsType<SendResult.Accepted>(Assert.Single(await _commands.ResultsAsync(command.Id)));
        Assert.Equal(call[1]!.GetValue<string>(), accepted.VendorReference);
    }

    // ocpp-gateway: "Charger refuses".
    [Fact]
    public async Task Refused_start_publishes_a_rejected_result()
    {
        using var charger = await ConnectAsync();
        var command = GatewayCommands.Command(CommandType.StartCharging);

        await _commands.SendAsync(command);
        var call = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Rejected"}]""");

        Assert.Equal("Rejected", Assert.IsType<SendResult.Rejected>(Assert.Single(await _commands.ResultsAsync(command.Id))).Reason);
    }

    // ocpp-gateway: "Start delivered twice".
    [Fact]
    public async Task Start_delivered_twice_sends_one_remote_start()
    {
        using var charger = await ConnectAsync();
        var command = GatewayCommands.Command(CommandType.StartCharging);
        await _commands.SendAsync(command);
        var call = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");
        await _commands.ResultsAsync(command.Id);

        // The charger starts the transaction, then the same command arrives again.
        await charger.CallAsync($$"""[2,"st1","StartTransaction",{"connectorId":1,"idTag":"CHEAPHOURS","meterStart":1000,"timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}"}]""");
        await _commands.SendAsync(command);

        var results = await _commands.ResultsAsync(command.Id, count: 2);
        Assert.All(results, r => Assert.IsType<SendResult.Accepted>(r));
        Assert.Equal(CommandVendorReferences.AlreadyInEffect, ((SendResult.Accepted)results[1]).VendorReference);
        await AssertNoCallAsync(charger);
    }

    // ocpp-gateway: "Stop for an idle charger".
    [Fact]
    public async Task Stop_for_an_idle_charger_sends_no_call()
    {
        using var charger = await ConnectAsync();
        var command = GatewayCommands.Command(CommandType.StopCharging);

        await _commands.SendAsync(command);

        var accepted = Assert.IsType<SendResult.Accepted>(Assert.Single(await _commands.ResultsAsync(command.Id)));
        Assert.Equal(CommandVendorReferences.AlreadyInEffect, accepted.VendorReference);
        await AssertNoCallAsync(charger);
    }

    // ocpp-gateway: "Stale registry entry".
    [Fact]
    public async Task Command_for_a_charger_this_replica_does_not_hold_is_transient()
    {
        var command = GatewayCommands.Command(CommandType.StartCharging);

        await _commands.SendAsync(command, chargePointId: "CP002");

        Assert.IsType<SendResult.Transient>(Assert.Single(await _commands.ResultsAsync(command.Id)));
    }

    [Fact]
    public async Task Power_limit_without_a_session_sets_the_default_profile()
    {
        using var charger = await ConnectAsync();
        var command = GatewayCommands.Command(CommandType.SetPowerLimit, 3700);

        await _commands.SendAsync(command);
        var call = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");

        Assert.Equal("SetChargingProfile", call[2]!.GetValue<string>());
        Assert.Equal("TxDefaultProfile", call[3]!["csChargingProfiles"]!["chargingProfilePurpose"]!.GetValue<string>());
        Assert.IsType<SendResult.Accepted>(Assert.Single(await _commands.ResultsAsync(command.Id)));
    }

    // The next frame after a heartbeat must be the heartbeat's answer, not a call from the gateway.
    private static async Task AssertNoCallAsync(OcppTestClient charger)
    {
        var next = await charger.CallAsync("""[2,"probe","Heartbeat",{}]""");
        Assert.Equal((3, "probe"), (next[0]!.GetValue<int>(), next[1]!.GetValue<string>()));
    }

    private async Task<OcppTestClient> ConnectAsync()
    {
        var charger = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        return charger;
    }
}
