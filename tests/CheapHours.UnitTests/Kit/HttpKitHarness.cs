using System.Net;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.UnitTests.Kit;

/// <summary>A stand-in vendor: answers from a function and counts requests.</summary>
internal sealed class StubVendor(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubVendor Always(HttpStatusCode status) => new((_, _) => new HttpResponseMessage(status));

    public static StubVendor Sequence(params HttpStatusCode[] statuses) =>
        new((_, n) => new HttpResponseMessage(statuses[Math.Min(n, statuses.Length - 1)]));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _count) - 1;
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(answer(request, n));
    }
}

internal sealed class TestHttpOptionsA : HttpProviderOptions;

internal sealed class TestHttpOptionsB : HttpProviderOptions;

internal sealed class HttpProviderA(ProviderHttpClient<HttpProviderA> client) : IPollingProvider
{
    public string ProviderId => "provia";

    public Capabilities Capabilities => Capabilities.ReadTelemetry;

    public ProviderHttpClient<HttpProviderA> Client { get; } = client;

    public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        Task.FromResult<PollResult>(new PollResult.Completed([]));
}

internal sealed class HttpProviderB(ProviderHttpClient<HttpProviderB> client) : IPollingProvider
{
    public string ProviderId => "provib";

    public Capabilities Capabilities => Capabilities.ReadTelemetry;

    public ProviderHttpClient<HttpProviderB> Client { get; } = client;

    public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        Task.FromResult<PollResult>(new PollResult.Completed([]));
}

/// <summary>Builds a service provider with HTTP providers on stub vendors.</summary>
internal static class HttpKitHarness
{
    public static readonly ProviderAccount AccountA = new("provia", OwnerId.Parse("own_demo"), "acct_1");

    public static readonly ProviderAccount AccountB = new("provib", OwnerId.Parse("own_demo"), "acct_1");

    /// <summary>Settings that keep tests fast: tiny retry delays, a breaker that never opens unless a test lowers it.</summary>
    public static Dictionary<string, string?> FastSettings(string section) => new()
    {
        [$"{section}:BaseAddress"] = "https://vendor.example/",
        [$"{section}:Http:RetryBaseDelay"] = "00:00:00.001",
        [$"{section}:Http:MinimumThroughput"] = "1000",
        [$"{section}:RateLimit:RequestsPerMinute"] = "600",
    };

    public static ServiceProvider Build(
        StubVendor vendorA,
        StubVendor? vendorB = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var values = FastSettings("Providers:A");
        foreach (var (key, value) in FastSettings("Providers:B"))
        {
            values[key] = value;
        }

        settings?.Invoke(values);
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        collection.AddLogging();
        services?.Invoke(collection);

        collection.AddProvider<HttpProviderA, TestHttpOptionsA>("Providers:A");
        collection.AddHttpClient(typeof(HttpProviderA).FullName!).ConfigurePrimaryHttpMessageHandler(() => vendorA);
        if (vendorB is not null)
        {
            collection.AddProvider<HttpProviderB, TestHttpOptionsB>("Providers:B");
            collection.AddHttpClient(typeof(HttpProviderB).FullName!).ConfigurePrimaryHttpMessageHandler(() => vendorB);
        }

        return collection.BuildServiceProvider();
    }

    public static HttpRequestMessage Get() => new(HttpMethod.Get, "api/v1/vehicles");
}
