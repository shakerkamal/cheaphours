using System.Net.Http.Headers;
using System.Text.Json;
using CheapHours.Simulators.CarCloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>The car-cloud simulator on real Kestrel (a free local port) with the test's clock.</summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    public const string ClientId = "cheaphours";
    public const string ClientSecret = "sim-test-secret-4f1c";
    public const string SeedA = "sim-test-seed-a-9e2b";
    public const string SeedB = "sim-test-seed-b-71d0";

    private readonly WebApplication _app;

    private SimulatorHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        Client = new HttpClient { BaseAddress = baseAddress };
    }

    public Uri BaseAddress { get; }

    public HttpClient Client { get; }

    /// <summary>Settings for two accounts: <c>acct_a</c> with <c>veh_a</c> (77 kWh, 11 kW, 40 %) and <c>acct_b</c> with <c>veh_b</c>.</summary>
    public static Dictionary<string, string> DefaultSettings(bool admin = true, int requestsPerMinute = 60, bool longTokens = true) => new()
    {
        ["Simulator:ClientId"] = ClientId,
        ["Simulator:ClientSecret"] = ClientSecret,
        ["Simulator:AccessTokenLifetime"] = longTokens ? "1.00:00:00" : "00:15:00", // long enough for tests that move the clock by hours
        ["Simulator:RequestsPerMinute"] = requestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Simulator:Accounts:0:Id"] = "acct_a",
        ["Simulator:Accounts:0:RefreshToken"] = SeedA,
        ["Simulator:Accounts:0:Vehicles:0:Id"] = "veh_a",
        ["Simulator:Accounts:0:Vehicles:0:BatteryCapacityKwh"] = "77",
        ["Simulator:Accounts:0:Vehicles:0:MaxChargePowerKw"] = "11",
        ["Simulator:Accounts:0:Vehicles:0:SocPercent"] = "40",
        ["Simulator:Accounts:1:Id"] = "acct_b",
        ["Simulator:Accounts:1:RefreshToken"] = SeedB,
        ["Simulator:Accounts:1:Vehicles:0:Id"] = "veh_b",
        ["Admin:Enabled"] = admin ? "true" : "false",
    };

    public static async Task<SimulatorHost> StartAsync(TimeProvider time, Dictionary<string, string>? settings = null)
    {
        var args = (settings ?? DefaultSettings()).Select(kv => $"--{kv.Key}={kv.Value}").Append("--urls=http://127.0.0.1:0").ToArray();
        var app = CarCloudSimulator.Build(args, time);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new SimulatorHost(app, new Uri(address + "/"));
    }

    /// <summary>Refreshes with the refresh-token grant.</summary>
    public async Task<(int Status, JsonElement Body)> RefreshAsync(string refreshToken, string clientSecret = ClientSecret)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = clientSecret,
        });
        using var response = await Client.PostAsync(new Uri("oauth/token", UriKind.Relative), content, TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await BodyAsync(response));
    }

    /// <summary>Gets an access token for the seed of <c>acct_a</c> (or another seed).</summary>
    public async Task<string> AccessTokenAsync(string seed = SeedA)
    {
        var (status, body) = await RefreshAsync(seed);
        Assert.Equal(200, status);
        return body.GetProperty("access_token").GetString()!;
    }

    public async Task<(int Status, JsonElement Body, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path, string? accessToken, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await BodyAsync(response), response);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
