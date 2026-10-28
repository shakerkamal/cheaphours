using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewayTelemetryTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private const string Heartbeat = """[2,"h1","Heartbeat",{}]""";

    private static string MeterValues(string id, params (string Timestamp, string Kwh)[] entries) =>
        $$"""[2,"{{id}}","MeterValues",{"connectorId":1,"transactionId":7,"meterValue":[""" +
        string.Join(",", entries.Select(e => $$"""{"timestamp":"{{e.Timestamp}}","sampledValue":[{"value":"{{e.Kwh}}","measurand":"Energy.Active.Import.Register","unit":"kWh"}]}""")) +
        "]}]";

    private static async Task<TelemetryReceived?> TakeAsync(string connectionString, string queue)
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var got = await channel.BasicGetAsync(queue, autoAck: true, ct);
        return got is null ? null : JsonSerializer.Deserialize<TelemetryReceived>(got.Body.Span, CheapHoursJson.Options);
    }

    private static void AssertResult(JsonArray reply, string uniqueId)
    {
        Assert.Equal(3, reply[0]!.GetValue<int>());
        Assert.Equal(uniqueId, reply[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Meter_values_are_published_as_one_message_before_the_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var client = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);

        var reply = await client.CallAsync(MeterValues("m1", ("2026-10-23T10:00:00Z", "12.50"), ("2026-10-23T10:00:15Z", "12.55"), ("2026-10-23T10:00:30Z", "12.60")));

        AssertResult(reply, "m1");
        Assert.Empty(reply[2]!.AsObject());

        // The gateway reads frames only once the device exists, so after the first answer it is there.
        var device = (await gateway.Devices.FindAsync("ocpp", "CP001", ct))!;

        // The broker confirmed the message before the answer was sent, so it is already in the queue.
        var message = await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main);
        Assert.NotNull(message);
        Assert.Equal(device.Id, message.DeviceId);
        Assert.Equal("ocpp", message.ProviderId);
        Assert.Equal([12_500L, 12_550L, 12_600L], message.Readings.Select(r => r.EnergyWh));
        Assert.All(message.Readings, r => Assert.Null(r.PowerW));
        Assert.Null(await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main));
    }

    [Fact]
    public async Task A_charging_session_is_accepted_and_its_meter_start_and_stop_are_published()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var client = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);

        var start = await client.CallAsync("""[2,"t1","StartTransaction",{"connectorId":1,"idTag":"ANY","meterStart":0,"timestamp":"2026-10-23T09:00:00Z"}]""");
        AssertResult(start, "t1");
        Assert.Equal("Accepted", start[2]!["idTagInfo"]!["status"]!.GetValue<string>());
        var transactionId = start[2]!["transactionId"]!.GetValue<int>();
        Assert.True(transactionId > 0);
        Assert.Equal(0, (await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main))!.Readings.Single().EnergyWh);

        var stop = await client.CallAsync($$"""[2,"t2","StopTransaction",{"transactionId":{{transactionId}},"meterStop":15000,"timestamp":"2026-10-23T09:30:00Z"}]""");
        AssertResult(stop, "t2");
        Assert.Equal("Accepted", stop[2]!["idTagInfo"]!["status"]!.GetValue<string>());
        Assert.Equal(15_000, (await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main))!.Readings.Single().EnergyWh);
    }

    [Fact]
    public async Task Meter_values_while_the_broker_is_down_get_an_internal_error_and_the_connection_stays_open()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: new Dictionary<string, string?> { ["ConnectionStrings:RabbitMq"] = broker.ConnectionString });
        await gateway.StartAsync();
        using var client = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await broker.StopAsync();

        var watch = Stopwatch.StartNew();
        var reply = await client.CallAsync(MeterValues("m2", ("2026-10-23T10:00:00Z", "1.0")));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"Took {watch.Elapsed}.");
        Assert.Equal(4, reply[0]!.GetValue<int>());
        Assert.Equal("m2", reply[1]!.GetValue<string>());
        Assert.Equal("InternalError", reply[2]!.GetValue<string>());
        Assert.False(gateway.Logs.Contains(new Uri(broker.ConnectionString).UserInfo.Split(':')[1]));

        AssertResult(await client.CallAsync(Heartbeat), "h1");
    }
}
