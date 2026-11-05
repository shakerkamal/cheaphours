using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Locks;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>The polling lease on <c>locks</c> (design §5).</summary>
public sealed class PollingLeaseTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero));
    private IMongoDatabase _database = null!;

    private NamedLease Lease => new(_database, _time, LeaseNames.DevicePolling);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A database per test: the lease is one document per database.
        _database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(nameof(PollingLeaseTests)));
        await MongoIndexes.EnsureAsync(_database, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Two_holders_contend_and_exactly_one_wins()
    {
        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Lease.TryAcquireAsync(i % 2 == 0 ? "replica-a" : "replica-b", Duration, Ct)));

        var winners = attempts.Select((won, i) => (won, holder: i % 2 == 0 ? "replica-a" : "replica-b")).Where(x => x.won).Select(x => x.holder).Distinct();
        Assert.Single(winners);
    }

    [Fact]
    public async Task The_holder_renews()
    {
        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));
        _time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));
        _time.Advance(TimeSpan.FromSeconds(20)); // 40 s after the first acquire, 20 s after the renew
        Assert.False(await Lease.TryAcquireAsync("replica-b", Duration, Ct));
    }

    [Fact]
    public async Task Another_holder_takes_over_after_expiry()
    {
        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));
        Assert.False(await Lease.TryAcquireAsync("replica-b", Duration, Ct));

        _time.Advance(Duration + TimeSpan.FromSeconds(1));

        Assert.True(await Lease.TryAcquireAsync("replica-b", Duration, Ct));
        Assert.False(await Lease.TryAcquireAsync("replica-a", Duration, Ct));
    }

    [Fact]
    public async Task Release_lets_the_other_holder_acquire()
    {
        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));

        await Lease.ReleaseAsync("replica-a", Ct);

        Assert.True(await Lease.TryAcquireAsync("replica-b", Duration, Ct));
    }

    [Fact]
    public async Task A_release_by_a_non_holder_does_nothing()
    {
        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));

        await Lease.ReleaseAsync("replica-b", Ct);

        Assert.False(await Lease.TryAcquireAsync("replica-b", Duration, Ct));
        Assert.True(await Lease.TryAcquireAsync("replica-a", Duration, Ct));
    }
}
