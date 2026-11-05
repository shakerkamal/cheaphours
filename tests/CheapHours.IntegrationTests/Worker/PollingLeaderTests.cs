using System.Collections.Concurrent;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Worker;

/// <summary>
/// Two worker replicas on one database (design §5): only the lease holder polls, a stopped leader is replaced,
/// and a stored pause outlives a change of leader. Short lease settings keep the class under 20 s.
/// </summary>
public sealed class PollingLeaderTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly ProviderAccount Account = new("counting", OwnerId.Parse("own_demo"), "acct_c");

    private readonly string _database = MongoFixture.NewDatabaseName(nameof(PollingLeaderTests));
    private readonly string _prefix = RabbitMqFixture.NewPrefix(nameof(PollingLeaderTests));
    private readonly ConcurrentQueue<(string Replica, DateTimeOffset At)> _polls = new();

    private IMongoDatabase Database => mongo.Client.GetDatabase(_database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await MongoIndexes.EnsureAsync(Database, Ct);
        await new ProviderAccountStore(Database, TimeProvider.System).SeedAsync(Account, "not-an-oauth-account", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<IHost> StartReplicaAsync(string replica, bool releasesLease = true)
    {
        var settings = WorkerHost.FastPolling(replica);
        settings["Providers:Counting:Enabled"] = "true";
        return WorkerHost.StartAsync(mongo, rabbit, _database, _prefix, settings, configure: services =>
        {
            services.AddSingleton(new CountingProvider(replica, _polls));
            services.AddProvider<CountingProvider>("Providers:Counting");
            services.AddSingleton(new ProviderPollingSettings(typeof(CountingProvider), Interval, TimeSpan.FromSeconds(1)));
            if (!releasesLease)
            {
                services.AddKeyedSingleton<ILease>(LeaseNames.DevicePolling, (sp, _) =>
                    new LeaseWithoutRelease(new NamedLease(sp.GetRequiredService<IMongoDatabase>(), TimeProvider.System, LeaseNames.DevicePolling)));
            }
        });
    }

    private Task<bool> FirstPollByAsync(string replica, TimeSpan within) =>
        WorkerHost.EventuallyAsync(() => Task.FromResult(_polls.Any(p => p.Replica == replica)), seen => seen, within);

    [Fact]
    public async Task Two_replicas_poll_the_account_once_per_interval()
    {
        var a = await StartReplicaAsync("replica-a");
        var b = await StartReplicaAsync("replica-b");
        await WorkerHost.EventuallyAsync(() => Task.FromResult(_polls.Count), n => n >= 1, TimeSpan.FromSeconds(5));
        var from = DateTimeOffset.UtcNow;

        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        var polls = _polls.Where(p => p.At >= from).ToList();
        Assert.Single(_polls.Select(p => p.Replica).Distinct());
        Assert.InRange(polls.Count, 3, 5);
        var times = _polls.Select(p => p.At).Order().ToList();
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(800), $"Two polls {pair.Second - pair.First} apart."));
        await Task.WhenAll(WorkerHost.StopAsync(a), WorkerHost.StopAsync(b));
    }

    [Fact]
    public async Task A_leader_that_stops_without_releasing_is_replaced_after_its_lease()
    {
        var a = await StartReplicaAsync("replica-a", releasesLease: false);
        await FirstPollByAsync("replica-a", TimeSpan.FromSeconds(5));
        var b = await StartReplicaAsync("replica-b");

        await WorkerHost.StopAsync(a);
        var stoppedAt = DateTimeOffset.UtcNow;
        await FirstPollByAsync("replica-b", TimeSpan.FromSeconds(6));

        var takeover = _polls.First(p => p.Replica == "replica-b").At - stoppedAt;
        Assert.True(takeover <= TimeSpan.FromSeconds(2 + 0.5 + 1 + 0.5), $"Took over after {takeover}."); // lease + acquire + interval + slack
        await WorkerHost.StopAsync(b);
    }

    [Fact]
    public async Task A_graceful_shutdown_hands_over_within_an_acquire_interval()
    {
        var a = await StartReplicaAsync("replica-a");
        await FirstPollByAsync("replica-a", TimeSpan.FromSeconds(5));
        var b = await StartReplicaAsync("replica-b");

        await WorkerHost.StopAsync(a);
        var stoppedAt = DateTimeOffset.UtcNow;
        await FirstPollByAsync("replica-b", TimeSpan.FromSeconds(4));

        var takeover = _polls.First(p => p.Replica == "replica-b").At - stoppedAt;
        Assert.True(takeover <= TimeSpan.FromSeconds(0.5 + 1 + 0.5), $"Took over after {takeover}."); // acquire + interval + slack, no lease wait
        await WorkerHost.StopAsync(b);
    }

    [Fact]
    public async Task A_new_leader_honours_a_stored_pause()
    {
        var a = await StartReplicaAsync("replica-a", releasesLease: false);
        await FirstPollByAsync("replica-a", TimeSpan.FromSeconds(5));
        await WorkerHost.StopAsync(a);
        var pausedUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        await new ProviderAccountStore(Database, TimeProvider.System).RecordPollAsync(Account, pausedUntil, pausedUntil, Ct);

        var b = await StartReplicaAsync("replica-b");
        await FirstPollByAsync("replica-b", TimeSpan.FromSeconds(8));

        var first = _polls.First(p => p.Replica == "replica-b").At;
        Assert.True(first >= pausedUntil.AddMilliseconds(-50), $"Polled {pausedUntil - first} before the pause ended.");
        await WorkerHost.StopAsync(b);
    }

    /// <summary>A polling provider that only records which replica polled when.</summary>
    private sealed class CountingProvider(string replica, ConcurrentQueue<(string Replica, DateTimeOffset At)> polls) : IPollingProvider
    {
        public string ProviderId => "counting";

        public Capabilities Capabilities => Capabilities.ReadTelemetry;

        public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken)
        {
            polls.Enqueue((replica, DateTimeOffset.UtcNow));
            return Task.FromResult<PollResult>(new PollResult.Completed([]));
        }
    }

    /// <summary>Stands for a replica that dies: its lease is never released.</summary>
    private sealed class LeaseWithoutRelease(ILease inner) : ILease
    {
        public string Name => inner.Name;

        public Task<bool> TryAcquireAsync(string holder, TimeSpan duration, CancellationToken cancellationToken) =>
            inner.TryAcquireAsync(holder, duration, cancellationToken);

        public Task ReleaseAsync(string holder, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
