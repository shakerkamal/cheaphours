using System.Net;
using CheapHours.Core.Devices;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using CheapHours.Simulators.HeatPumpCloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.ContractTests.HeatPumpCloud;

/// <summary>
/// The simulator and the provider agree: webhooks captured from the real simulator verify and translate with the
/// real provider, so the documented vendor scheme is what both sides implement.
/// </summary>
public sealed class HeatPumpCloudSimulatorConformanceTests : IAsyncLifetime
{
    private const string Secret = "conformance-heat-pump-signing-secret";
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly Capture _capture = new();
    private readonly HeatPumpCloudProvider _provider = new(Options.Create(new HeatPumpCloudOptions { SigningSecret = Secret }));
    private WebApplication _sim = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _sim = HeatPumpCloudSimulator.Build(
            [
                "--Simulator:CallbackUrl=http://gateway.test/webhooks/heatpumpcloud/acct_demo",
                $"--Simulator:SigningSecret={Secret}",
                "--Simulator:Interval=01:00:00",
                "--Admin:Enabled=true",
                "--urls=http://127.0.0.1:0",
            ],
            _time,
            _capture);
        await _sim.StartAsync(TestContext.Current.CancellationToken);
        var address = _sim.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _admin = new HttpClient { BaseAddress = new Uri(address + "/") };
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _sim.StopAsync();
        await _sim.DisposeAsync();
    }

    private async Task<InboundMessage> NextAsync(int count)
    {
        for (var i = 0; i < 100 && _capture.Received.Count < count; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(_capture.Received.Count >= count, $"The simulator delivered {_capture.Received.Count} webhook(s), expected {count}.");
        var (headers, body) = _capture.Received[count - 1];
        return new InboundMessage(null, body, headers, _time.GetUtcNow());
    }

    [Fact]
    public async Task A_state_delivery_verifies_and_translates_to_a_heat_pump_reading()
    {
        _time.Advance(TimeSpan.FromHours(1));
        var message = await NextAsync(1);

        Assert.True(_provider.Verify(message));
        var at = Start.AddHours(1);
        Assert.Equal(
            [
                new DeviceEvent.Discovered("hp_1", at, DeviceType.HeatPump, new DeviceAttributes()),
                new DeviceEvent.Connected("hp_1", at, new DeviceHardware("Kaltwerk", "AeroHeat 9")),
                new DeviceEvent.StatusChanged("hp_1", at, DeviceStatus.Available),
                new DeviceEvent.ReadingReported(new DeviceReading("hp_1", at, 3_000, 1_003_000)),
            ],
            _provider.Translate(message));
    }

    [Fact]
    public async Task A_redelivery_verifies_and_translates_to_the_same_reading()
    {
        _time.Advance(TimeSpan.FromHours(1));
        var first = await NextAsync(1);
        _time.Advance(TimeSpan.FromSeconds(30));

        using var response = await _admin.PostAsync(new Uri("admin/heat-pumps/hp_1/redeliver", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var again = await NextAsync(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_provider.Verify(again));
        Assert.Equal(_provider.Translate(first), _provider.Translate(again));
    }

    [Fact]
    public async Task A_removal_verifies_and_translates_to_device_removed()
    {
        using var response = await _admin.PostAsync(new Uri("admin/heat-pumps/hp_1/remove", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var message = await NextAsync(1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_provider.Verify(message));
        Assert.Equal([new DeviceEvent.DeviceRemoved("hp_1", Start)], _provider.Translate(message));
    }

    [Fact]
    public async Task A_mode_change_shows_in_the_status()
    {
        using var response = await _admin.PostAsync(new Uri("admin/heat-pumps/hp_1/mode/Fault", UriKind.Relative), null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(1));
        var message = await NextAsync(1);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DeviceStatus.Faulted, Assert.Single(_provider.Translate(message).OfType<DeviceEvent.StatusChanged>()).Status);
    }

    /// <summary>Stands in for the gateway: keeps the headers and the raw body of every delivery and answers 202.</summary>
    private sealed class Capture : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<(Dictionary<string, string> Headers, byte[] Body)> _received = [];

        public IReadOnlyList<(Dictionary<string, string> Headers, byte[] Body)> Received
        {
            get
            {
                lock (_lock)
                {
                    return [.. _received];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.Concat(request.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                _received.Add((headers, body));
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
