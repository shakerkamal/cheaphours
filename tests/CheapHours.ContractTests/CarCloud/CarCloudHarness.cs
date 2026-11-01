using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>The car-cloud provider against <see cref="CarCloudVendorFake"/>, with short timeouts.</summary>
internal sealed class CarCloudHarness : IProviderContractHarness
{
    public static readonly DateTimeOffset StateTime = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    public CarCloudVendorFake Vendor { get; } = new();

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Providers:CarCloud:BaseAddress"] = Vendor.Server.BaseAddress.ToString(),
        ["Providers:CarCloud:TokenEndpoint"] = Vendor.Server.TokenEndpoint.ToString(),
        ["Providers:CarCloud:ClientId"] = WireMockVendorFake.ClientId,
        ["Providers:CarCloud:ClientSecret"] = Vendor.Server.ClientSecret,
        ["Providers:CarCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
        ["Providers:CarCloud:Accounts:0:RefreshToken"] = Vendor.Server.SeedRefreshToken,
        ["Providers:CarCloud:Http:AttemptTimeout"] = "00:00:01", // generous: the full suite runs in parallel and slows WireMock down
        ["Providers:CarCloud:Http:TotalTimeout"] = "00:00:03",
        ["Providers:CarCloud:Http:RetryBaseDelay"] = "00:00:00.010",
        ["Providers:CarCloud:Http:MinimumThroughput"] = "1000",
        ["Providers:CarCloud:RateLimit:RequestsPerMinute"] = "6000",
    };

    public bool UsesHttp => true;

    public bool UsesOAuth => true;

    public ProviderAccount? Account { get; } = new(CarCloudProvider.Id, OwnerId.Parse("own_demo"), "acct_demo");

    public int VendorRequestCount => Vendor.Server.VendorRequests.Count;

    public int TokenRequestCount => Vendor.Server.TokenRequests.Count;

    public (InboundMessage Input, DeviceReading Expected)? KnownInput => null;

    public InboundMessage? UnhandledInput => null;

    public InboundMessage? MalformedInput => null;

    public void Register(IServiceCollection services) => services.AddProvider<CarCloudProvider, CarCloudOptions>(CarCloudOptions.SectionName);

    public Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync()
    {
        Vendor.AddVehicle("veh_1");
        Vendor.SetState("veh_1", "2026-10-28T19:00:00+01:00", chargingState: "Charging", soc: 42.5m, powerKw: 11m, sessionKwh: 3.2505m);
        return Task.FromResult(("veh_1", new DeviceReading("veh_1", StateTime, 11_000, 3_251, 42.5m)));
    }

    public Task<string> ArrangeRemovedDeviceAsync()
    {
        Vendor.AddVehicle("veh_gone");
        Vendor.Remove("veh_gone");
        return Task.FromResult("veh_gone");
    }

    public Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null)
    {
        Vendor.Server.FailWith(status, retryAfter, body);
        return Task.CompletedTask;
    }

    public Task ArrangeDelayAsync(TimeSpan delay)
    {
        Vendor.Server.DelayBy(delay);
        return Task.CompletedTask;
    }

    public Task ArrangeAccessTokenRejectedAsync()
    {
        Vendor.Server.RejectCurrentAccessTokens();
        return Task.CompletedTask;
    }

    public Task ArrangeRefreshRevokedAsync()
    {
        Vendor.Server.RevokeAll();
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> CommandKeysSentFor(string externalId) =>
    [
        .. Vendor.Server.VendorRequests
            .Where(r => r.Method == "POST" && r.Path.StartsWith($"/api/v1/vehicles/{externalId}/charging/", StringComparison.Ordinal))
            .Select(r => r.Header("Idempotency-Key") ?? string.Empty),
    ];

    public ValueTask DisposeAsync()
    {
        Vendor.Dispose();
        return ValueTask.CompletedTask;
    }
}
