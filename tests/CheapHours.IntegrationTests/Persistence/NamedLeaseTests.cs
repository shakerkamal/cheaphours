using CheapHours.Host.Goals;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Locks;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>Named leases and the leased run of a scheduled job (design §6).</summary>
public sealed class NamedLeaseTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly string _suffix = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await SharedDatabases.GetAsync(mongo, nameof(NamedLeaseTests));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Two_holders_of_the_same_name_run_the_job_once()
    {
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Job(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            await gate.Task.WaitAsync(ct);
        }

        var a = LeasedRun.RunAsync(Lease(LeaseNames.PriceImport), "replica-a", Duration, _time, Job, Ct);
        var b = LeasedRun.RunAsync(Lease(LeaseNames.PriceImport), "replica-b", Duration, _time, Job, Ct);

        // The loser returns at once, before the winner's job is let go.
        var loser = await Task.WhenAny(a, b);
        Assert.False(await loser);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, runs);
        Assert.Single(results, ran => ran);
    }

    [Fact]
    public async Task Different_names_do_not_block_each_other()
    {
        Assert.True(await Lease(LeaseNames.PriceImport).TryAcquireAsync("replica-a", Duration, Ct));

        var ran = await LeasedRun.RunAsync(Lease(LeaseNames.DevicePolling), "replica-b", Duration, _time, _ => Task.CompletedTask, Ct);

        Assert.True(ran);
        Assert.False(await Lease(LeaseNames.PriceImport).TryAcquireAsync("replica-b", Duration, Ct));
    }

    [Fact]
    public async Task Lease_is_released_after_the_job()
    {
        Assert.True(await LeasedRun.RunAsync(Lease(LeaseNames.PriceImport), "replica-a", Duration, _time, _ => Task.CompletedTask, Ct));

        Assert.True(await Lease(LeaseNames.PriceImport).TryAcquireAsync("replica-b", Duration, Ct));
    }

    [Fact]
    public async Task Lease_is_released_when_the_job_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LeasedRun.RunAsync(Lease(LeaseNames.PriceImport), "replica-a", Duration, _time, _ => throw new InvalidOperationException("boom"), Ct));

        Assert.True(await Lease(LeaseNames.PriceImport).TryAcquireAsync("replica-b", Duration, Ct));
    }

    [Fact]
    public async Task Job_is_cancelled_at_half_the_lease()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = LeasedRun.RunAsync(Lease(LeaseNames.DevicePolling), "replica-a", Duration, _time, async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, Ct);

        await started.Task.WaitAsync(Ct);
        _time.Advance(Duration / 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(await Lease(LeaseNames.DevicePolling).TryAcquireAsync("replica-b", Duration, Ct));
    }

    // Each test keeps its leases apart from the others in the shared database by a suffix.
    private NamedLease Lease(string name) => new(_database, _time, name + "-" + _suffix);
}
