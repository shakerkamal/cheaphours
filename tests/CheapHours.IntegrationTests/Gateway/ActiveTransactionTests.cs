using System.Globalization;
using CheapHours.Core.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// The charger's active transaction, stored in <c>devices</c> (ocpp-gateway spec: the gateway knows each charger's
/// active transaction, and the stored id survives a gateway restart).
/// </summary>
public sealed class ActiveTransactionTests(MongoFixture mongo) : IAsyncLifetime
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
    public async Task Accepted_start_stores_the_transaction_and_its_stop_clears_it()
    {
        using var charger = await ConnectAsync(_gateway);

        var started = await charger.CallAsync(Start("st1"));
        var transactionId = started[2]!["transactionId"]!.GetValue<int>();

        Assert.Equal(transactionId.ToString(CultureInfo.InvariantCulture), await ActiveSessionAsync(_gateway));

        await charger.CallAsync(Stop("sp0", transactionId + 1));
        Assert.Equal(transactionId.ToString(CultureInfo.InvariantCulture), await ActiveSessionAsync(_gateway));

        await charger.CallAsync(Stop("sp1", transactionId));
        Assert.Null(await ActiveSessionAsync(_gateway));
    }

    // ocpp-gateway: "Charger restarted without its transaction".
    [Fact]
    public async Task Charger_reconnecting_available_without_a_stop_leaves_no_transaction_and_a_start_is_sent()
    {
        await using var commands = await GatewayCommands.StartAsync(_gateway);
        using (var charger = await ConnectAsync(_gateway))
        {
            await charger.CallAsync(Start("st1"));
        }

        using var restarted = await ConnectAsync(_gateway);
        await restarted.CallAsync($$"""[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Available","timestamp":"{{Now()}}"}]""");

        Assert.Null(await ActiveSessionAsync(_gateway));
        var stop = GatewayCommands.Command(CommandType.StopCharging);
        await commands.SendAsync(stop);
        Assert.Equal(CommandVendorReferences.AlreadyInEffect, Assert.IsType<SendResult.Accepted>(Assert.Single(await commands.ResultsAsync(stop.Id))).VendorReference);
        var start = GatewayCommands.Command(CommandType.StartCharging);
        await commands.SendAsync(start);
        var call = await restarted.ReceiveAsync();
        Assert.Equal("RemoteStartTransaction", call![2]!.GetValue<string>());
    }

    [Fact]
    public async Task Meter_values_naming_a_transaction_mark_it_running()
    {
        using var charger = await ConnectAsync(_gateway);

        await charger.CallAsync($$"""[2,"mv1","MeterValues",{"connectorId":1,"transactionId":4242,"meterValue":[{"timestamp":"{{Now()}}","sampledValue":[{"value":"1500"}]}]}]""");

        Assert.Equal("4242", await ActiveSessionAsync(_gateway));
    }

    // ocpp-gateway: "Stop after a gateway restart".
    [Fact]
    public async Task Stop_after_a_gateway_restart_names_the_running_transaction()
    {
        int transactionId;
        using (var charger = await ConnectAsync(_gateway))
        {
            var started = await charger.CallAsync(Start("st1"));
            transactionId = started[2]!["transactionId"]!.GetValue<int>();
        }

        await _gateway.DisposeAsync();
        _gateway = new GatewayFactory(mongo.ConnectionString) { DatabaseName = _gateway.DatabaseName };
        await _gateway.StartAsync();
        await using var commands = await GatewayCommands.StartAsync(_gateway);

        // The charger reconnects without booting again, mid-transaction; an answered heartbeat shows the session runs.
        using var reconnected = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await reconnected.CallAsync("""[2,"h1","Heartbeat",{}]""");
        var stop = GatewayCommands.Command(CommandType.StopCharging);
        await commands.SendAsync(stop);
        var call = await reconnected.ReceiveAsync();
        await reconnected.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");

        Assert.Equal("RemoteStopTransaction", call[2]!.GetValue<string>());
        Assert.Equal(transactionId, call[3]!["transactionId"]!.GetValue<int>());
        Assert.IsType<SendResult.Accepted>(Assert.Single(await commands.ResultsAsync(stop.Id)));
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Start(string uniqueId) =>
        $$"""[2,"{{uniqueId}}","StartTransaction",{"connectorId":1,"idTag":"CHEAPHOURS","meterStart":1000,"timestamp":"{{Now()}}"}]""";

    private static string Stop(string uniqueId, int transactionId) =>
        $$"""[2,"{{uniqueId}}","StopTransaction",{"transactionId":{{transactionId}},"meterStop":2000,"timestamp":"{{Now()}}"}]""";

    private static async Task<OcppTestClient> ConnectAsync(GatewayFactory gateway)
    {
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        return charger;
    }

    private static async Task<string?> ActiveSessionAsync(GatewayFactory gateway) =>
        (await gateway.Services.GetRequiredService<IDeviceCommandStateStore>().FindAsync("ocpp", "CP001", Ct))?.ActiveSessionId;
}
