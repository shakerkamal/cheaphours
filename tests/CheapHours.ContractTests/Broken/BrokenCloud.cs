using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.ContractTests.Broken;

/// <summary>Options of the deliberately broken providers.</summary>
internal sealed class BrokenCloudOptions : HttpProviderOptions;

/// <summary>
/// A minimal vendor for the broken providers: <c>GET /api/devices</c> lists one device with a reading, and
/// <c>POST /api/devices/{id}/{command}</c> accepts any command. It proves the suite catches broken providers.
/// </summary>
internal sealed class BrokenCloudHarness<TProvider>(Action<IServiceCollection> register) : IProviderContractHarness
    where TProvider : class, IDeviceProvider
{
    public static readonly DateTimeOffset ReadingTime = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private readonly WireMockVendorFake _vendor = Start();

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Providers:Broken:BaseAddress"] = _vendor.BaseAddress.ToString(),
        ["Providers:Broken:Http:AttemptTimeout"] = "00:00:01", // generous: the full suite runs in parallel and slows WireMock down
        ["Providers:Broken:Http:TotalTimeout"] = "00:00:03",
        ["Providers:Broken:Http:RetryBaseDelay"] = "00:00:00.010",
        ["Providers:Broken:Http:MinimumThroughput"] = "1000",
        ["Providers:Broken:RateLimit:RequestsPerMinute"] = "6000",
    };

    public bool UsesHttp => true;

    public bool UsesOAuth => false;

    public ProviderAccount? Account { get; } = new("broken", OwnerId.Parse("own_demo"), "acct_broken");

    public int VendorRequestCount => _vendor.VendorRequests.Count;

    public int TokenRequestCount => _vendor.TokenRequests.Count;

    public (InboundMessage Input, DeviceReading Expected)? KnownInput => null;

    public InboundMessage? UnhandledInput => null;

    public InboundMessage? MalformedInput => null;

    public void Register(IServiceCollection services) => register(services);

    public Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync() =>
        Task.FromResult(("dev-1", new DeviceReading("dev-1", ReadingTime, null, 1000)));

    public Task<string> ArrangeRemovedDeviceAsync() => throw new NotSupportedException();

    public Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null)
    {
        _vendor.FailWith(status, retryAfter, body);
        return Task.CompletedTask;
    }

    public Task ArrangeDelayAsync(TimeSpan delay)
    {
        _vendor.DelayBy(delay);
        return Task.CompletedTask;
    }

    public Task ArrangeAccessTokenRejectedAsync() => throw new NotSupportedException();

    public Task ArrangeRefreshRevokedAsync() => throw new NotSupportedException();

    public IReadOnlyList<string> CommandKeysSentFor(string externalId) =>
        [.. _vendor.VendorRequests.Where(r => r.Path.StartsWith($"/api/devices/{externalId}/", StringComparison.Ordinal)).Select(r => r.Header("Idempotency-Key") ?? string.Empty)];

    public ValueTask DisposeAsync()
    {
        _vendor.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WireMockVendorFake Start()
    {
        var vendor = new WireMockVendorFake(oauth: false);
        vendor.Map(HttpMethod.Get, "^/api/devices$", (_, _) => VendorAnswer.Raw(200, """{"devices":[{"id":"dev-1","energyWh":1000,"ts":"2026-10-28T18:00:00Z"}]}"""));
        vendor.Map(HttpMethod.Post, "^/api/devices/[^/]+/[^/]+$", (_, _) => VendorAnswer.Raw(202, """{"requestId":"req-1"}"""));
        return vendor;
    }
}

/// <summary>Reads the broken vendor's device list.</summary>
internal static class BrokenCloudReadings
{
    public static IReadOnlyList<DeviceEvent> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return
        [
            .. document.RootElement.GetProperty("devices").EnumerateArray().Select(d => new DeviceEvent.ReadingReported(new DeviceReading(
                d.GetProperty("id").GetString()!,
                d.GetProperty("ts").GetDateTimeOffset(),
                null,
                d.GetProperty("energyWh").GetInt64()))),
        ];
    }

    public static Func<HttpRequestMessage> Start(DeviceCommand command, DeviceAddress address) => () =>
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{address.ExternalId}/{command.Type}");
        request.Headers.Add("Idempotency-Key", command.IdempotencyKey);
        return request;
    };
}
