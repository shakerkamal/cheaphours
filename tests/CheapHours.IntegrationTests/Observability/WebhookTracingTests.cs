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
/// A webhook's readings travel in the trace of its request, and its logs name the provider and the device
/// (distributed-tracing: "Webhook traced"; structured-logging: "Device data logs name the device and provider").
/// </summary>
public sealed class WebhookTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private const string Path = "/webhooks/heatpumpcloud/acct_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_state_webhook_publishes_its_reading_in_the_trace_of_the_request()
    {
        using var spans = new SpanRecorder();
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: HeatPumpWebhooks.Settings());
        await gateway.StartAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = await TakeTelemetryAsync(gateway);
        var context = ActivityContext.Parse(message.Traceparent!, null);
        var request = await spans.WaitForAsync(a => a.TraceId == context.TraceId && a.Source.Name == "Microsoft.AspNetCore");
        Assert.Equal(ActivityKind.Server, request.Kind);
        Assert.Equal(Path, request.GetTagItem("url.path"));
        Assert.Equal(request.SpanId, context.SpanId);

        var accepted = Assert.Single(gateway.Logs.Entries, e => e.Message.StartsWith("Accepted a heatpumpcloud webhook", StringComparison.Ordinal));
        Assert.Equal("heatpumpcloud", accepted.Scopes["providerId"]);
        Assert.Equal(context.TraceId.ToString(), accepted.Scopes["TraceId"]);
    }

    [Fact]
    public async Task Log_entries_about_a_webhook_device_name_the_provider_and_the_device()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: HeatPumpWebhooks.Settings());
        await gateway.StartAsync();
        using var client = gateway.CreateClient();
        using (await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State()))
        {
        }

        const string Deleted = """{"eventId":"evt_9","type":"device.deleted","occurredAt":"2026-11-02T10:05:00Z","accountId":"acct_demo","device":{"id":"hp_1"}}""";
        using var response = await HeatPumpWebhooks.PostAsync(client, Deleted);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var removed = Assert.Single(gateway.Logs.Entries, e => e.Message.Contains("as removed", StringComparison.Ordinal));
        Assert.Equal("heatpumpcloud", removed.Scopes["providerId"]);
        Assert.Equal("hp_1", removed.Scopes["externalId"]);
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
