using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;

namespace CheapHours.IntegrationTests.Api;

/// <summary><c>GET /v1/commands/{id}</c> against the API role on real MongoDB (command-api spec).</summary>
public sealed class CommandReadEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // command-api: "Own command".
    [Fact]
    public async Task Own_command_gets_200_with_its_state_and_history_oldest_first()
    {
        var id = await CreateAsync("""{"type":"SetPowerLimit","parameters":{"maxPowerW":3700}}""");
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var command = await client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/commands/{id}", UriKind.Relative), Ct);

        Assert.Equal(id.Value, command.GetProperty("id").GetString());
        Assert.Equal("SetPowerLimit", command.GetProperty("type").GetString());
        Assert.Equal(3700, command.GetProperty("parameters").GetProperty("maxPowerW").GetInt32());
        Assert.Equal(0, command.GetProperty("attempts").GetInt32());
        Assert.StartsWith("dev_", command.GetProperty("deviceId").GetString(), StringComparison.Ordinal);
        var history = command.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal("Pending", Assert.Single(history).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("reason").ValueKind);
        Assert.Equal(command.GetProperty("state").GetString(), history[^1].GetProperty("state").GetString());
    }

    // command-api: "Another owner's command".
    [Fact]
    public async Task Another_owners_command_gets_404()
    {
        var id = await CreateAsync("""{"type":"StartCharging"}""");
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        using var response = await client.GetAsync(new Uri($"/v1/commands/{id}", UriKind.Relative), Ct);
        using var missing = await client.GetAsync(new Uri("/v1/commands/cmd_missing", UriKind.Relative), Ct);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (response.StatusCode, missing.StatusCode));
        var foreign = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var unknown = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.EndsWith("/not-found", foreign.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(unknown.GetProperty("detail").GetString(), foreign.GetProperty("detail").GetString());
    }

    // command-api: "Cancelled by a new plan version".
    [Fact]
    public async Task Cancelled_command_shows_the_reason_on_its_last_history_entry()
    {
        var id = await CreateAsync("""{"type":"StartCharging"}""");
        var store = new CommandStore(shared.Database);
        var stored = (await store.FindAsync(id, Ct))!;
        stored.Command.Cancel(DateTimeOffset.UtcNow, "Superseded by plan_8812:v4");
        await store.SaveAsync(stored, Ct);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var command = await client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/commands/{id}", UriKind.Relative), Ct);

        Assert.Equal("Cancelled", command.GetProperty("state").GetString());
        var last = command.GetProperty("history").EnumerateArray().Last();
        Assert.Equal(("Cancelled", "Superseded by plan_8812:v4"), (last.GetProperty("state").GetString(), last.GetProperty("reason").GetString()));
    }

    private async Task<CommandId> CreateAsync(string json)
    {
        var wallbox = await new DeviceStateStore(shared.Database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), OwnerId.Parse("own_a"), "ocpp", $"ext-{Guid.NewGuid():N}", DeviceType.Charger, Capabilities.StartStop | Capabilities.SetPowerLimit, new DeviceAttributes(maxPowerW: 11_000)),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            Ct);
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/devices/{wallbox}/commands", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return CommandId.Parse((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!);
    }
}
