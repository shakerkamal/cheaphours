using System.Text.Json;
using CheapHours.Simulators.HeatPumpCloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>The heat-pump-cloud simulator on Kestrel (a real port for its admin endpoints), delivering through a handler of the test's choice.</summary>
public sealed class HeatPumpSimulatorHost : IAsyncDisposable
{
    /// <summary>The signing secret the simulator is started with.</summary>
    public const string Secret = "integration-heat-pump-signing-secret";

    private readonly WebApplication _app;

    private HeatPumpSimulatorHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        Client = new HttpClient { BaseAddress = baseAddress };
    }

    /// <summary>Gets a client for the admin endpoints.</summary>
    public HttpClient Client { get; }

    /// <summary>The default settings: one heat pump <c>hp_1</c> at 3 kW and 1000 kWh, heating, delivering every hour.</summary>
    public static Dictionary<string, string> DefaultSettings(string callbackUrl = "http://receiver.test/webhooks/heatpumpcloud/acct_demo", bool admin = true) => new()
    {
        ["Simulator:CallbackUrl"] = callbackUrl,
        ["Simulator:SigningSecret"] = Secret,
        ["Simulator:Interval"] = "01:00:00",
        ["Admin:Enabled"] = admin ? "true" : "false",
    };

    /// <summary>Starts the simulator.</summary>
    public static async Task<HeatPumpSimulatorHost> StartAsync(TimeProvider time, Dictionary<string, string>? settings = null, HttpMessageHandler? handler = null)
    {
        var args = (settings ?? DefaultSettings()).Select(kv => $"--{kv.Key}={kv.Value}").Append("--urls=http://127.0.0.1:0").ToArray();
        var app = HeatPumpCloudSimulator.Build(args, time, handler);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HeatPumpSimulatorHost(app, new Uri(address + "/"));
    }

    /// <summary>Posts to an admin endpoint.</summary>
    public async Task<(int Status, JsonElement Body)> PostAsync(string path)
    {
        using var response = await Client.PostAsync(new Uri(path, UriKind.Relative), null, TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await BodyAsync(response));
    }

    /// <summary>Gets an admin endpoint.</summary>
    public async Task<(int Status, JsonElement Body)> GetAsync(string path)
    {
        using var response = await Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await BodyAsync(response));
    }

    /// <inheritdoc/>
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
