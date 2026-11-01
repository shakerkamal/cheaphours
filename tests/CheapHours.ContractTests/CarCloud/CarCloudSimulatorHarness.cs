using System.Collections.Concurrent;
using System.Security.Cryptography;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using CheapHours.Simulators.CarCloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>
/// The car-cloud provider against the real simulator (in-process on Kestrel). The simulator's clock is frozen,
/// so its state, and the reading it gives, is exact; moving it expires the simulator's access tokens. A recorder
/// on the provider's clients probes what the vendor received.
/// </summary>
internal sealed class CarCloudSimulatorHarness : IProviderContractHarness
{
    public static readonly DateTimeOffset FrozenAt = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(FrozenAt);
    private readonly ConcurrentQueue<VendorRequest> _vendorRequests = new();
    private readonly ConcurrentQueue<VendorRequest> _tokenRequests = new();
    private readonly string _clientSecret = "sim-secret-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
    private readonly string _seed = "sim-seed-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
    private readonly WebApplication _simulator;
    private readonly Uri _baseAddress;

    public CarCloudSimulatorHarness()
    {
        _simulator = CarCloudSimulator.Build(
        [
            "--Simulator:ClientId=cheaphours",
            $"--Simulator:ClientSecret={_clientSecret}",
            "--Simulator:RequestsPerMinute=6000",
            "--Simulator:Accounts:0:Id=acct_demo",
            $"--Simulator:Accounts:0:RefreshToken={_seed}",
            "--Simulator:Accounts:0:Vehicles:0:Id=veh_1",
            "--Simulator:Accounts:0:Vehicles:0:SocPercent=42.5",
            "--Admin:Enabled=true",
            "--urls=http://127.0.0.1:0",
        ],
        _clock);
        _simulator.StartAsync().GetAwaiter().GetResult();
        _baseAddress = new Uri(_simulator.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Providers:CarCloud:BaseAddress"] = _baseAddress.ToString(),
        ["Providers:CarCloud:TokenEndpoint"] = new Uri(_baseAddress, "oauth/token").ToString(),
        ["Providers:CarCloud:ClientId"] = "cheaphours",
        ["Providers:CarCloud:ClientSecret"] = _clientSecret,
        ["Providers:CarCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
        ["Providers:CarCloud:Accounts:0:RefreshToken"] = _seed,
        ["Providers:CarCloud:Http:MinimumThroughput"] = "1000",
        ["Providers:CarCloud:RateLimit:RequestsPerMinute"] = "6000",
    };

    public bool UsesHttp => true;

    public bool UsesOAuth => true;

    public ProviderAccount? Account { get; } = new(CarCloudProvider.Id, OwnerId.Parse("own_demo"), "acct_demo");

    public int VendorRequestCount => _vendorRequests.Count;

    public int TokenRequestCount => _tokenRequests.Count;

    public (InboundMessage Input, DeviceReading Expected)? KnownInput => null;

    public InboundMessage? UnhandledInput => null;

    public InboundMessage? MalformedInput => null;

    public void Register(IServiceCollection services)
    {
        services.AddProvider<CarCloudProvider, CarCloudOptions>(CarCloudOptions.SectionName);
        services.AddHttpClient(typeof(CarCloudProvider).FullName!).AddHttpMessageHandler(() => new Recorder(_vendorRequests));
        services.AddHttpClient(typeof(CarCloudProvider).FullName + ".token").AddHttpMessageHandler(() => new Recorder(_tokenRequests));
    }

    /// <summary>The simulator's vehicle stands plugged in at 42.5 %: no power, no session energy, the frozen time.</summary>
    public Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync() =>
        Task.FromResult(("veh_1", new DeviceReading("veh_1", FrozenAt, null, 0, 42.5m)));

    public Task<string> ArrangeRemovedDeviceAsync() => throw new NotSupportedException();

    public Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null) => throw new NotSupportedException();

    public Task ArrangeDelayAsync(TimeSpan delay) => throw new NotSupportedException();

    /// <summary>Moves the simulator's clock past the access-token lifetime: issued tokens expire, the refresh token still works.</summary>
    public Task ArrangeAccessTokenRejectedAsync()
    {
        _clock.Advance(TimeSpan.FromMinutes(16));
        return Task.CompletedTask;
    }

    public async Task ArrangeRefreshRevokedAsync()
    {
        using var client = new HttpClient { BaseAddress = _baseAddress };
        using var response = await client.PostAsync(new Uri("admin/accounts/acct_demo/revoke", UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public IReadOnlyList<string> CommandKeysSentFor(string externalId) =>
    [
        .. _vendorRequests
            .Where(r => r.Method == "POST" && r.Path.StartsWith($"/api/v1/vehicles/{externalId}/charging/", StringComparison.Ordinal))
            .Select(r => r.Header("Idempotency-Key") ?? string.Empty),
    ];

    public async ValueTask DisposeAsync()
    {
        await _simulator.StopAsync();
        await _simulator.DisposeAsync();
    }

    /// <summary>Records what the provider sends (each attempt), without the body.</summary>
    private sealed class Recorder(ConcurrentQueue<VendorRequest> log) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            log.Enqueue(new VendorRequest(request.Method.Method, request.RequestUri!.AbsolutePath, headers, string.Empty));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
