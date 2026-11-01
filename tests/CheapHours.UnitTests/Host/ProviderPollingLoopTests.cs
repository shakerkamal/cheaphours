using CheapHours.Core.Ids;
using CheapHours.Host.Polling;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Host;

/// <summary>One provider's polling loop (design §5), driven by a fake clock.</summary>
public sealed class ProviderPollingLoopTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly InMemoryPollingSchedule _schedule = new();
    private readonly ScriptedPollingProvider _provider = new();
    private readonly CountingPollRecorder _recorder = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    public ProviderPollingLoopTests() => _provider.Clock = _time;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        _stop.Dispose();
    }

    private static ProviderAccount Account(string id) => new("scripted", OwnerId.Parse("own_demo"), id);

    private void Start(int maxConcurrentPolls = 4)
    {
        var loop = new ProviderPollingLoop(
            _provider,
            new ProviderPollingSettings(typeof(ScriptedPollingProvider), Interval, TimeSpan.FromSeconds(10)),
            _schedule,
            _recorder,
            new PollingOptions { Tick = TimeSpan.FromSeconds(1), MaxConcurrentPolls = maxConcurrentPolls },
            _time,
            NullLogger.Instance);
        _running = loop.RunAsync(_stop.Token);
    }

    /// <summary>Advances the clock one tick at a time, letting the loop run between ticks.</summary>
    private async Task AdvanceAsync(TimeSpan by)
    {
        for (var moved = TimeSpan.Zero; moved < by; moved += TimeSpan.FromSeconds(1))
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task A_completed_poll_is_due_again_one_interval_later()
    {
        _schedule.Add(Account("acct_1"));
        Start();

        await EventuallyAsync(() => _schedule.Of("acct_1").NextPollAt is not null);
        Assert.Equal(T0 + Interval, _schedule.Of("acct_1").NextPollAt);
        Assert.Equal(["acct_1"], _recorder.Recorded);

        await AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.Equal(1, _provider.Polls);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => _provider.Polls == 2);
        Assert.Equal(T0 + Interval, _provider.Log.Last().At);
    }

    [Fact]
    public async Task An_account_needing_reconnection_is_not_polled()
    {
        _schedule.Add(Account("acct_1"), reconnectNeeded: true);
        Start();

        await AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, _provider.Polls);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_interval_pauses_the_account()
    {
        _schedule.Add(Account("acct_1"));
        _provider.Answer = (_, _) => Task.FromResult<PollResult>(new PollResult.Failed("vendor-rate-limited", TimeSpan.FromSeconds(120)));
        Start();

        await EventuallyAsync(() => _schedule.Of("acct_1").NextPollAt is not null);
        Assert.Equal((T0.AddSeconds(120), T0.AddSeconds(120)), _schedule.Of("acct_1"));

        await AdvanceAsync(TimeSpan.FromSeconds(119));
        Assert.Equal(1, _provider.Polls);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => _provider.Polls == 2);
    }

    [Fact]
    public void A_failure_without_retry_after_waits_one_interval()
    {
        var (next, paused) = ProviderPollingLoop.Schedule(T0, Interval, new PollResult.Failed("vendor returned 503"));

        Assert.Equal(T0 + Interval, next);
        Assert.Null(paused);
        Assert.Equal((T0 + Interval, (DateTimeOffset?)T0.AddSeconds(10)), ProviderPollingLoop.Schedule(T0, Interval, new PollResult.Failed("x", TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task No_second_poll_starts_while_one_is_in_flight()
    {
        var release = new TaskCompletionSource<PollResult>();
        _schedule.Add(Account("acct_1"));
        _provider.Answer = (_, _) => release.Task;
        Start();
        await EventuallyAsync(() => _provider.Polls == 1);

        await AdvanceAsync(TimeSpan.FromSeconds(5)); // the account is still due: it was never rescheduled

        Assert.Equal(1, _provider.Polls);
        release.SetResult(new PollResult.Completed([]));
        await EventuallyAsync(() => _schedule.Of("acct_1").NextPollAt is not null);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_polls_run_at_once()
    {
        var release = new TaskCompletionSource<PollResult>();
        for (var i = 0; i < 6; i++)
        {
            _schedule.Add(Account($"acct_{i}"));
        }

        _provider.Answer = (_, _) => release.Task;
        Start(maxConcurrentPolls: 4);
        await EventuallyAsync(() => _provider.Polls == 4);

        await AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, _provider.Polls);

        release.SetResult(new PollResult.Completed([]));
        await AdvanceAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => _provider.Polls == 6);
    }

    [Fact]
    public async Task A_throwing_provider_is_scheduled_as_a_failure_and_the_loop_keeps_running()
    {
        _schedule.Add(Account("acct_1"));
        _provider.Answer = (_, _) => throw new InvalidOperationException("provider bug");
        Start();

        await EventuallyAsync(() => _schedule.Of("acct_1").NextPollAt is not null);
        Assert.Equal(T0 + Interval, _schedule.Of("acct_1").NextPollAt);

        _provider.Answer = (_, _) => Task.FromResult<PollResult>(new PollResult.Completed([]));
        await AdvanceAsync(Interval);
        await EventuallyAsync(() => _provider.Polls == 2);
        await EventuallyAsync(() => _recorder.Recorded.Count == 1);
    }
}
