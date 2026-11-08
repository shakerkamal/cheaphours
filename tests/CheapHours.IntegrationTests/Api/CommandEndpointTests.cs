using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>POST /v1/devices/{id}/commands</c> against the API role on real MongoDB (command-api spec). Each test seeds its
/// own devices, so the tests share one host and database.
/// </summary>
public sealed class CommandEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommandStore Commands => new(shared.Database);

    // command-api: "Power limit"; command-dispatch: "Saved together".
    [Fact]
    public async Task Power_limit_gets_202_with_its_location_and_is_saved_pending_with_its_outbox_entry()
    {
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"SetPowerLimit","parameters":{"maxPowerW":3700}}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = CommandId.Parse(body.GetProperty("id").GetString()!);
        Assert.Equal("Pending", body.GetProperty("state").GetString());
        Assert.Equal($"/v1/commands/{id}", response.Headers.Location!.OriginalString);
        var stored = await Commands.FindAsync(id, Ct);
        Assert.Equal((CommandState.Pending, CommandType.SetPowerLimit, (int?)3700), (stored!.Command.State, stored.Command.Request.Type, stored.Command.Request.MaxPowerW));
        Assert.Equal(1, await shared.Database.GetCollection<BsonDocument>(MongoIndexes.Outbox).CountDocumentsAsync(new BsonDocument("commandId", id.Value), cancellationToken: Ct));
    }

    // command-api: "Read key".
    [Fact]
    public async Task Read_key_gets_403()
    {
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"StartCharging"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await Commands.ListOpenForDeviceAsync(wallbox, Ct));
    }

    // command-api: "Limit without a value".
    [Fact]
    public async Task Limit_without_a_value_gets_400_for_the_field_and_saves_nothing()
    {
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"SetPowerLimit"}""");

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("parameters.maxPowerW", out _));
        Assert.Empty(await Commands.ListOpenForDeviceAsync(wallbox, Ct));
    }

    // command-api: "Another owner's wallbox".
    [Fact]
    public async Task Another_owners_wallbox_gets_404_and_saves_nothing()
    {
        var foreign = await WallboxAsync(OwnerId.Parse("own_b"));
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, foreign, """{"type":"StartCharging"}""");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
        Assert.Empty(await Commands.ListOpenForDeviceAsync(foreign, Ct));
    }

    // command-api: "Heat pump has no start".
    [Fact]
    public async Task Heat_pump_start_gets_422_capability_not_supported()
    {
        var heatPump = await DeviceAsync(OwnerA, "heatpumpcloud", DeviceType.HeatPump, Capabilities.ReadTelemetry, new DeviceAttributes());
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, heatPump, """{"type":"StartCharging"}""");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "capability-not-supported");
        Assert.Empty(await Commands.ListOpenForDeviceAsync(heatPump, Ct));
    }

    // command-api: "Above the charger's maximum".
    [Fact]
    public async Task Limit_above_the_chargers_maximum_gets_422_unprocessable()
    {
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"SetPowerLimit","parameters":{"maxPowerW":22000}}""");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "unprocessable");
        Assert.Empty(await Commands.ListOpenForDeviceAsync(wallbox, Ct));
    }

    [Fact]
    public async Task Removed_device_gets_422_unprocessable()
    {
        var wallbox = await WallboxAsync();
        var device = await new DeviceStateStore(shared.Database).GetAsync(wallbox, Ct);
        await new DeviceStateStore(shared.Database).MarkRemovedAsync(device!.Device.ProviderId, device.Device.ExternalId, DateTimeOffset.UtcNow, Ct);
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"StartCharging"}""");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "unprocessable");
    }

    // command-api: "Client retries after a timeout".
    [Fact]
    public async Task Same_key_twice_gets_the_same_command_and_saves_one()
    {
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);
        var key = Guid.NewGuid().ToString();

        using var first = await SendAsync(client, wallbox, """{"type":"StartCharging"}""", key);
        using var second = await SendAsync(client, wallbox, """{"type":"StartCharging"}""", key);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, second.StatusCode));
        Assert.Equal(await IdAsync(first), await IdAsync(second));
        Assert.Single(await Commands.ListOpenForDeviceAsync(wallbox, Ct));
    }

    // command-dispatch: "Traced request".
    [Fact]
    public async Task Command_and_its_announcement_keep_the_requests_trace_id()
    {
        const string TraceId = "0af7651916cd43dd8448eb211c80319c";
        var wallbox = await WallboxAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await SendAsync(client, wallbox, """{"type":"StartCharging"}""", traceparent: $"00-{TraceId}-b7ad6b7169203331-01");

        var id = await IdAsync(response);
        var stored = await Commands.FindAsync(CommandId.Parse(id), Ct);
        Assert.Contains(TraceId, stored!.Traceparent, StringComparison.Ordinal);
        var entry = await shared.Database.GetCollection<BsonDocument>(MongoIndexes.Outbox).Find(new BsonDocument("commandId", id)).SingleAsync(Ct);
        Assert.Contains(TraceId, entry["traceparent"].AsString, StringComparison.Ordinal);
    }

    private static async Task<string> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, DeviceId device, string json, string? idempotencyKey = null, string? traceparent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/devices/{device}/commands", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        if (traceparent is not null)
        {
            request.Headers.Add("traceparent", traceparent);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.EndsWith("/" + type, problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        return problem;
    }

    private Task<DeviceId> WallboxAsync(OwnerId? owner = null) =>
        DeviceAsync(owner ?? OwnerA, "ocpp", DeviceType.Charger, Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit, new DeviceAttributes(maxPowerW: 11_000));

    private Task<DeviceId> DeviceAsync(OwnerId owner, string provider, DeviceType type, Capabilities capabilities, DeviceAttributes attributes) =>
        new DeviceStateStore(shared.Database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), owner, provider, $"ext-{Guid.NewGuid():N}", type, capabilities, attributes),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            Ct);
}
