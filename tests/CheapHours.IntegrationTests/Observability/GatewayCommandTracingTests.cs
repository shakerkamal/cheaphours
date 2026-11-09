using System.Diagnostics;
using CheapHours.Core.Commands;
using CheapHours.IntegrationTests.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// The gateway handles a command in the command's trace, and names the command in its logs (distributed-tracing:
/// "Charger command end to end"; structured-logging: "Gateway call fails").
/// </summary>
public sealed class GatewayCommandTracingTests(MongoFixture mongo)
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";

    [Fact]
    public async Task An_answered_call_is_a_client_span_under_the_command_handling_span_of_the_command_trace()
    {
        using var spans = new SpanRecorder();
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        await using var commands = await GatewayCommands.StartAsync(gateway);
        using var charger = await ConnectAsync(gateway);
        var traceparent = SpanRecorder.NewTraceparent();
        var stored = ActivityContext.Parse(traceparent, null);
        var command = GatewayCommands.Command(CommandType.StartCharging);

        await commands.SendAsync(command, traceparent: traceparent);
        var call = await charger.ReceiveAsync();
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");
        await commands.ResultsAsync(command.Id);

        var trace = await spans.WaitForTraceAsync(stored.TraceId, ["ocpp RemoteStartTransaction"]);
        var process = Assert.Single(trace, a => a.Kind == ActivityKind.Consumer);
        var ocpp = Assert.Single(trace, a => a.DisplayName == "ocpp RemoteStartTransaction");
        Assert.Equal(stored.SpanId, process.ParentSpanId);
        Assert.Equal($"{commands.Queue} process", process.DisplayName);
        Assert.Equal(process.SpanId, ocpp.ParentSpanId);
        Assert.Equal(ActivityKind.Client, ocpp.Kind);
        Assert.Equal("CP001", ocpp.GetTagItem("cheaphours.charge_point.id"));
        Assert.Equal("RemoteStartTransaction", ocpp.GetTagItem("ocpp.action"));
        Assert.Equal("ocpp", ocpp.GetTagItem("cheaphours.provider.id"));
        Assert.Equal("success", ocpp.GetTagItem("cheaphours.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, ocpp.Status);

        // The result goes back to the worker in the same trace.
        await spans.WaitForAsync(a => a.TraceId == stored.TraceId && a.Kind == ActivityKind.Producer);
    }

    [Fact]
    public async Task A_call_that_times_out_is_an_error_span_and_its_log_entry_names_the_command_device_and_provider()
    {
        using var spans = new SpanRecorder();
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: new Dictionary<string, string?> { ["Gateway:Commands:CallTimeout"] = "00:00:01" });
        await gateway.StartAsync();
        await using var commands = await GatewayCommands.StartAsync(gateway);
        using var charger = await ConnectAsync(gateway);
        var traceparent = SpanRecorder.NewTraceparent();
        var command = GatewayCommands.Command(CommandType.StopCharging);

        // The charger has an active transaction (it would make a stop already in effect otherwise), and stays silent.
        await charger.CallAsync($$"""[2,"st1","StartTransaction",{"connectorId":1,"idTag":"CHEAPHOURS","meterStart":0,"timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}"}]""");
        await commands.SendAsync(command, traceparent: traceparent);
        await charger.ReceiveAsync();

        Assert.IsType<SendResult.Transient>(Assert.Single(await commands.ResultsAsync(command.Id)));
        var trace = await spans.WaitForTraceAsync(SpanRecorder.TraceIdOf(traceparent), ["ocpp RemoteStopTransaction"]);
        var ocpp = Assert.Single(trace, a => a.DisplayName == "ocpp RemoteStopTransaction");
        Assert.Equal(ActivityStatusCode.Error, ocpp.Status);
        Assert.Equal("transient", ocpp.GetTagItem("cheaphours.outcome"));

        var logged = Assert.Single(gateway.Logs.Entries, e => e.Message.Contains("did not answer RemoteStopTransaction", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal(command.Id.Value, logged.Scopes["commandId"]);
        Assert.Equal(command.DeviceId.Value, logged.Scopes["deviceId"]);
        Assert.Equal("ocpp", logged.Scopes["providerId"]);
        Assert.Equal(ocpp.TraceId.ToString(), logged.Scopes["TraceId"]);
        Assert.Equal(ocpp.SpanId.ToString(), logged.Scopes["SpanId"]);
    }

    private static async Task<OcppTestClient> ConnectAsync(GatewayFactory gateway)
    {
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        return charger;
    }
}
