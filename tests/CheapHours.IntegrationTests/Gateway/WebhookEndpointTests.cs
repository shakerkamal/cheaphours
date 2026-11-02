using System.Net;
using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using CheapHours.Persistence;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>The gateway's webhook endpoint (design §1) on Kestrel, with the heat-pump-cloud provider, a real MongoDB and a real RabbitMQ.</summary>
public sealed class WebhookEndpointTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TelemetryReceived?> TakeAsync(string connectionString, string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var got = await channel.BasicGetAsync(queue, autoAck: true, Ct);
        return got is null ? null : JsonSerializer.Deserialize<TelemetryReceived>(got.Body.Span, CheapHoursJson.Options);
    }

    private async Task<GatewayFactory> StartGatewayAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = HeatPumpWebhooks.Settings();
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var gateway = new GatewayFactory(mongo.ConnectionString, overrides: settings);
        await gateway.StartAsync();
        return gateway;
    }

    private static async Task AssertNothingRecordedAsync(GatewayFactory gateway, string connectionString, string deviceId = "hp_1")
    {
        Assert.Null(await gateway.Devices.FindAsync("heatpumpcloud", deviceId, Ct));
        Assert.Null(await TakeAsync(connectionString, gateway.Topology.Telemetry.Main));
    }

    [Fact]
    public async Task A_verified_state_webhook_creates_the_heat_pump_under_the_owner_and_publishes_its_reading_before_the_answer()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        var device = await gateway.Devices.FindAsync("heatpumpcloud", "hp_1", Ct);
        Assert.NotNull(device);
        Assert.Equal(OwnerId.Parse("own_demo"), device.OwnerId);
        Assert.Equal(DeviceType.HeatPump, device.Type);
        Assert.Equal(Capabilities.ReadTelemetry, device.Capabilities);
        Assert.Equal(TimeSpan.FromSeconds(15), device.State?.ReportInterval);
        Assert.Equal("Kaltwerk", device.State?.Hardware?.Manufacturer);
        Assert.Equal(DeviceStatus.Available, device.State?.Status);

        var message = await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main);
        Assert.NotNull(message);
        Assert.Equal(device.Id, message.DeviceId);
        Assert.Equal("heatpumpcloud", message.ProviderId);
        var reading = Assert.Single(message.Readings);
        Assert.Equal(2_400, reading.PowerW);
        Assert.Equal(1_234_568, reading.EnergyWh);
        Assert.Null(await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main));
        Assert.True(gateway.Logs.Contains("Accepted a heatpumpcloud webhook for account acct_demo with 4 event(s)"));
    }

    [Fact]
    public async Task A_body_changed_after_signing_gets_401_and_nothing_is_recorded()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();
        var signature = HeatPumpWebhooks.Signature(HeatPumpWebhooks.State());

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(energyKwh: 9999m), signature: signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
        Assert.False(gateway.Logs.Contains(signature));
    }

    [Fact]
    public async Task A_webhook_without_a_signature_gets_401()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(), signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
    }

    [Fact]
    public async Task A_stale_signature_gets_401()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();
        var body = HeatPumpWebhooks.State();

        using var response = await HeatPumpWebhooks.PostAsync(client, body, signature: HeatPumpWebhooks.Signature(body, DateTimeOffset.UtcNow.AddMinutes(-6)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
    }

    [Theory]
    [InlineData("/webhooks/nosuchcloud/acct_demo")]
    [InlineData("/webhooks/ocpp/acct_demo")]
    public async Task An_unknown_provider_or_one_that_does_not_verify_webhooks_gets_404(string path)
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(), path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
    }

    [Fact]
    public async Task Without_the_provider_configured_its_webhooks_get_404()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_webhook_for_an_unconfigured_account_gets_404_and_a_warning()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(), "/webhooks/heatpumpcloud/acct_stranger");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
        Assert.Contains(gateway.Logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("heatpumpcloud", StringComparison.Ordinal) && l.Contains("acct_stranger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsigned_webhook_for_an_unconfigured_account_gets_401_so_accounts_cannot_be_probed()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(), "/webhooks/heatpumpcloud/acct_stranger", signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_64_KB_gets_413_before_the_signature_is_checked()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();
        var body = new string(' ', WebhookEndpoint.MaxBodyBytes) + HeatPumpWebhooks.State();

        using var unsigned = await HeatPumpWebhooks.PostAsync(client, body, signature: null);
        using var signed = await HeatPumpWebhooks.PostAsync(client, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, signed.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
    }

    [Fact]
    public async Task A_body_of_exactly_64_KB_is_read()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();
        var state = HeatPumpWebhooks.State();
        var body = state + new string(' ', WebhookEndpoint.MaxBodyBytes - state.Length);

        using var response = await HeatPumpWebhooks.PostAsync(client, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_body_that_is_not_json_gets_400_and_the_reason_is_logged()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, "this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(gateway.Logs.Contains("body is not JSON"));
        Assert.False(gateway.Logs.Contains("this is not json"));
    }

    [Fact]
    public async Task An_event_type_without_device_data_is_accepted_and_records_nothing()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();

        using var response = await HeatPumpWebhooks.PostAsync(client, """{"eventId":"evt_5","type":"account.updated","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(await TakeAsync(rabbit.ConnectionString, gateway.Topology.Telemetry.Main));
    }

    [Fact]
    public async Task A_webhook_after_shutdown_has_begun_gets_503_and_nothing_is_recorded()
    {
        await using var gateway = await StartGatewayAsync();
        using var client = gateway.CreateClient();
        gateway.Services.GetRequiredService<GatewayState>().BeginDraining();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertNothingRecordedAsync(gateway, rabbit.ConnectionString);
    }

    [Fact]
    public async Task A_verified_webhook_while_the_broker_is_down_gets_503_so_the_vendor_retries()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        await using var gateway = await StartGatewayAsync(new() { ["ConnectionStrings:RabbitMq"] = broker.ConnectionString });
        using var client = gateway.CreateClient();
        await broker.StopAsync();

        using var response = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(gateway.Logs.Contains(new Uri(broker.ConnectionString).UserInfo.Split(':')[1]));
    }

    [Fact]
    public async Task The_same_webhook_delivered_twice_is_stored_as_one_reading()
    {
        await using var gateway = await StartGatewayAsync();
        var worker = await WorkerHost.StartAsync(mongo, rabbit, gateway.DatabaseName, gateway.QueuePrefix, new Dictionary<string, string>());
        try
        {
            using var client = gateway.CreateClient();
            var body = HeatPumpWebhooks.State();

            using var first = await HeatPumpWebhooks.PostAsync(client, body);
            using var second = await HeatPumpWebhooks.PostAsync(client, body, signature: HeatPumpWebhooks.Signature(body, DateTimeOffset.UtcNow.AddSeconds(1)));
            using var later = await HeatPumpWebhooks.PostAsync(client, HeatPumpWebhooks.State(occurredAt: "2026-11-02T10:00:15Z", energyKwh: 1234.6m, eventId: "evt_02"));

            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
            var device = (await gateway.Devices.FindAsync("heatpumpcloud", "hp_1", Ct))!;
            var telemetry = mongo.Client.GetDatabase(gateway.DatabaseName).GetCollection<BsonDocument>(MongoIndexes.Telemetry);
            await WorkerHost.EventuallyAsync(
                () => telemetry.CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Id.Value), cancellationToken: Ct),
                n => n >= 2,
                TimeSpan.FromSeconds(30));

            await Task.Delay(TimeSpan.FromSeconds(1), Ct); // a duplicate stored late would have landed by now
            Assert.Equal(2, await telemetry.CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Id.Value), cancellationToken: Ct));
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
        }
    }
}
