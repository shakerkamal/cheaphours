using System.Collections.Concurrent;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.DependencyInjection;
using CapturingLoggerProvider = CheapHours.IntegrationTests.Infrastructure.CapturingLoggerProvider;

namespace CheapHours.IntegrationTests.Worker;

/// <summary>
/// The bulkhead between providers (design §5, task 4.6): while a test-only provider's vendor answers with 5 s
/// delays and then 500 until its breaker opens, the car-cloud provider is still polled on schedule.
/// </summary>
public sealed class PollingBulkheadTests(MongoFixture mongo, RabbitMqFixture rabbit) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly string _database = MongoFixture.NewDatabaseName(nameof(PollingBulkheadTests));
    private readonly string _prefix = RabbitMqFixture.NewPrefix(nameof(PollingBulkheadTests));
    private readonly string _keys = Directory.CreateTempSubdirectory("cheaphours-keys-").FullName;
    private readonly WireMockVendorFake _carCloud = new(oauth: true);
    private readonly WireMockVendorFake _slowCloud = new(oauth: false);
    private readonly ConcurrentQueue<DateTimeOffset> _carCloudPolls = new();
    private readonly CapturingLoggerProvider _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _carCloud.Dispose();
        _slowCloud.Dispose();
        _logs.Dispose();
        Directory.Delete(_keys, recursive: true);
    }

    [Fact]
    public async Task A_slow_failing_vendor_does_not_delay_another_providers_polls()
    {
        _carCloud.Map(HttpMethod.Get, "^/api/v1/vehicles$", (_, _) =>
        {
            _carCloudPolls.Enqueue(DateTimeOffset.UtcNow);
            return VendorAnswer.Raw(200, """{"vehicles":[]}""");
        });
        _slowCloud.Map(HttpMethod.Get, "^/api/things$", (_, _) => VendorAnswer.Raw(500, """{"error":"broken"}"""));
        _slowCloud.DelayBy(TimeSpan.FromSeconds(5));

        var database = mongo.Client.GetDatabase(_database);
        await MongoIndexes.EnsureAsync(database, Ct);
        await new ProviderAccountStore(database, TimeProvider.System).SeedAsync(new ProviderAccount("slowcloud", OwnerId.Parse("own_slow"), "acct_slow"), "not-an-oauth-account", Ct);

        var settings = WorkerHost.FastPolling("bulkhead");
        settings["Providers:CarCloud:BaseAddress"] = _carCloud.BaseAddress.ToString();
        settings["Providers:CarCloud:TokenEndpoint"] = _carCloud.TokenEndpoint.ToString();
        settings["Providers:CarCloud:ClientId"] = WireMockVendorFake.ClientId;
        settings["Providers:CarCloud:ClientSecret"] = _carCloud.ClientSecret;
        settings["Providers:CarCloud:Accounts:0:Owner"] = "own_demo";
        settings["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_demo";
        settings["Providers:CarCloud:Accounts:0:RefreshToken"] = _carCloud.SeedRefreshToken;
        settings["Providers:SlowCloud:BaseAddress"] = _slowCloud.BaseAddress.ToString();
        settings["Providers:SlowCloud:Http:AttemptTimeout"] = "00:00:00.300";
        settings["Providers:SlowCloud:Http:TotalTimeout"] = "00:00:01";
        settings["Providers:SlowCloud:Http:RetryBaseDelay"] = "00:00:00.010";
        settings["Providers:SlowCloud:Http:MinimumThroughput"] = "2";
        settings["DataProtection:KeysPath"] = _keys;

        var worker = await WorkerHost.StartAsync(mongo, rabbit, _database, _prefix, settings, _logs, services =>
        {
            services.AddProvider<SlowCloudProvider, SlowCloudOptions>("Providers:SlowCloud");
            // The 5 s minimum applies to configured intervals; the test wants 1 s for both providers.
            services.AddSingleton(new ProviderPollingSettings(typeof(CarCloudProvider), Interval, TimeSpan.FromSeconds(10)));
            services.AddSingleton(new ProviderPollingSettings(typeof(SlowCloudProvider), Interval, TimeSpan.FromSeconds(1)));
        });

        await WorkerHost.EventuallyAsync(() => Task.FromResult(_carCloudPolls.Count), n => n >= 1, TimeSpan.FromSeconds(10));
        var from = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(6), Ct);

        var polls = _carCloudPolls.Where(t => t >= from.AddSeconds(-1.5)).Order().ToList();
        Assert.True(polls.Count >= 5, $"Only {polls.Count} car-cloud polls in 6 s.");
        Assert.All(polls.Zip(polls.Skip(1)), pair =>
            Assert.True(pair.Second - pair.First <= Interval + Tick + TimeSpan.FromMilliseconds(400), $"Car-cloud polls {pair.Second - pair.First} apart."));
        Assert.True(_slowCloud.VendorRequests.Count >= 2, "The slow vendor was never called.");
        Assert.True(_logs.Contains("circuit-open"), "The slow provider's breaker did not open:\n" + string.Join('\n', _logs.Lines.Where(l => l.Contains("slowcloud", StringComparison.Ordinal))));
        await WorkerHost.StopAsync(worker);
    }

    /// <summary>Options of the test-only slow provider.</summary>
    private sealed class SlowCloudOptions : HttpProviderOptions;

    /// <summary>A test-only polling provider on the kit whose vendor is slow and failing.</summary>
    private sealed class SlowCloudProvider(ProviderHttpClient<SlowCloudProvider> vendor) : IPollingProvider
    {
        public string ProviderId => "slowcloud";

        public Capabilities Capabilities => Capabilities.ReadTelemetry;

        public async Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken)
        {
            using var response = await vendor.SendAsync(account, () => new HttpRequestMessage(HttpMethod.Get, "api/things"), cancellationToken);
            return response is VendorResponse.Failed failed ? failed.Failure.ToPollFailed() : new PollResult.Completed([]);
        }
    }
}
