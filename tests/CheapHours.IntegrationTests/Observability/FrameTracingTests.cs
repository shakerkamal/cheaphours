using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CheapHours.IntegrationTests.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using CheapHours.Tests.Observability;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// Each charger frame is a trace of its own and the readings it brings carry it; the long-lived /ocpp request and
/// health probes are not traced (distributed-tracing: "Device data starts a trace", "Health checks are not traced";
/// structured-logging: "Device data logs name the device and provider").
/// </summary>
public sealed class FrameTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // distributed-tracing: "Meter values traced".
    [Fact]
    public async Task Meter_values_are_handled_in_a_root_frame_span_whose_trace_the_published_readings_carry()
    {
        using var spans = new SpanRecorder();
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var charger = await ConnectAsync(gateway);

        await charger.CallAsync(MeterValues("m1", "Energy.Active.Import.Register", "kWh", "12.50"));

        var message = await TakeTelemetryAsync(gateway);
        var frame = await spans.WaitForAsync(a => a.DisplayName == "ocpp MeterValues" && Equals(a.GetTagItem("cheaphours.charge_point.id"), "CP001") && a.TraceId == SpanRecorder.TraceIdOf(message.Traceparent!));
        Assert.Equal(ActivityKind.Server, frame.Kind);
        Assert.Equal(default, frame.ParentSpanId);
        Assert.Equal("MeterValues", frame.GetTagItem("ocpp.action"));
        Assert.Equal("ocpp", frame.GetTagItem("cheaphours.provider.id"));
        Assert.Equal(frame.SpanId, ActivityContext.Parse(message.Traceparent!, null).SpanId);
    }

    // distributed-tracing: "Readiness probe"; the /ocpp request lasts the whole connection and is not traced either.
    [Fact]
    public async Task Neither_the_ocpp_request_nor_health_probes_create_spans()
    {
        using var spans = new SpanRecorder();
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using (var charger = await ConnectAsync(gateway))
        {
            await charger.CallAsync("""[2,"h1","Heartbeat",{}]""");
            await charger.Socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", Ct);
        }

        using var client = gateway.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await spans.WaitForAsync(a => a.DisplayName == "ocpp Heartbeat" && Equals(a.GetTagItem("cheaphours.charge_point.id"), "CP001"));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var requests = spans.All.Where(a => a.Source.Name == "Microsoft.AspNetCore").Select(a => $"{a.DisplayName} {a.GetTagItem("url.path")}").ToArray();
        Assert.DoesNotContain(requests, r => r.Contains("/health", StringComparison.Ordinal) || r.Contains("/ocpp", StringComparison.Ordinal));
    }

    // structured-logging: "Unusable meter sample".
    [Fact]
    public async Task The_log_entry_about_an_unusable_meter_sample_names_the_provider_and_the_charger()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var charger = await ConnectAsync(gateway);

        await charger.CallAsync(MeterValues("m2", "Temperature", "Celsius", "21.5"));

        var logged = Assert.Single(gateway.Logs.Entries, e => e.Message.Contains("no usable meter value", StringComparison.Ordinal));
        Assert.Equal("ocpp", logged.Scopes["providerId"]);
        Assert.Equal("CP001", logged.Scopes["externalId"]);
        Assert.True(logged.Scopes.ContainsKey("TraceId"));
    }

    private static string MeterValues(string id, string measurand, string unit, string value) =>
        $$"""[2,"{{id}}","MeterValues",{"connectorId":1,"meterValue":[{"timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}","sampledValue":[{"value":"{{value}}","measurand":"{{measurand}}","unit":"{{unit}}"}]}]}]""";

    private static async Task<OcppTestClient> ConnectAsync(GatewayFactory gateway)
    {
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        return charger;
    }

    private async Task<TelemetryReceived> TakeTelemetryAsync(GatewayFactory gateway)
    {
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        for (var i = 0; i < 50; i++)
        {
            if (await channel.BasicGetAsync(gateway.Topology.Telemetry.Main, autoAck: true, Ct) is { } got)
            {
                return JsonSerializer.Deserialize<TelemetryReceived>(got.Body.Span, CheapHoursJson.Options)!;
            }

            await Task.Delay(100, Ct);
        }

        throw new Xunit.Sdk.XunitException("No TelemetryReceived was published.");
    }
}
