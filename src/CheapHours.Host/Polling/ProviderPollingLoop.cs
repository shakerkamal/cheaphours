using System.Collections.Concurrent;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;

namespace CheapHours.Host.Polling;

/// <summary>
/// Polls one provider's due accounts (design §5): every tick it lists the accounts that are due and not already in
/// flight, and starts at most <see cref="PollingOptions.MaxConcurrentPolls"/> polls; a tick never waits for a poll.
/// Each poll runs under a deadline, its result is recorded, and the account's next poll is stored: one interval
/// later, or after the vendor's retry-after (stored as a pause) when that is longer. An exception from a provider
/// is logged and scheduled like a failed poll, so one bug cannot stop the loop. One loop per provider is the
/// bulkhead between providers.
/// </summary>
internal sealed partial class ProviderPollingLoop
{
    private readonly IPollingProvider _provider;
    private readonly ProviderPollingSettings _settings;
    private readonly IPollingSchedule _schedule;
    private readonly IPollRecorder _recorder;
    private readonly PollingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    public ProviderPollingLoop(
        IPollingProvider provider,
        ProviderPollingSettings settings,
        IPollingSchedule schedule,
        IPollRecorder recorder,
        PollingOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _provider = provider;
        _settings = settings;
        _schedule = schedule;
        _recorder = recorder;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets how many polls are running.</summary>
    public int InFlight => _inFlight.Count;

    /// <summary>When the account is next due, and until when it is paused, after a poll that started at <paramref name="started"/>.</summary>
    /// <param name="started">When the poll started.</param>
    /// <param name="interval">The provider's poll interval.</param>
    /// <param name="result">The poll's result.</param>
    /// <returns>The next poll time and the pause, if any.</returns>
    public static (DateTimeOffset Next, DateTimeOffset? PausedUntil) Schedule(DateTimeOffset started, TimeSpan interval, PollResult result) => result switch
    {
        PollResult.Failed { RetryAfter: { } retryAfter } => (started + (retryAfter > interval ? retryAfter : interval), started + retryAfter),
        _ => (started + interval, null),
    };

    /// <summary>Runs until <paramref name="stoppingToken"/> fires, then waits for the polls in flight.</summary>
    /// <param name="stoppingToken">Stops the loop (lease lost or shutdown).</param>
    /// <returns>A task that completes when the loop and its polls have stopped.</returns>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Tick, _time);
        try
        {
            do
            {
                await StartDuePollsAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_inFlight.Values).ConfigureAwait(false);
    }

    private async Task StartDuePollsAsync(CancellationToken stoppingToken)
    {
        var free = _options.MaxConcurrentPolls - _inFlight.Count;
        if (free <= 0)
        {
            return;
        }

        IReadOnlyList<ProviderAccount> due;
        try
        {
            due = await _schedule.ListDueAsync(_provider.ProviderId, _time.GetUtcNow(), [.. _inFlight.Keys], free, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListFailed(_provider.ProviderId, ex.GetType().Name);
            return;
        }

        foreach (var account in due)
        {
            var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(account.ExternalAccountId, poll.Task))
            {
                _ = RunPollAsync(account, poll, stoppingToken);
            }
        }
    }

    private async Task RunPollAsync(ProviderAccount account, TaskCompletionSource done, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            await PollAsync(account, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(account.ExternalAccountId, out _);
            done.TrySetResult();
        }
    }

    private async Task PollAsync(ProviderAccount account, CancellationToken stoppingToken)
    {
        var started = _time.GetUtcNow();
        PollResult result;
        using (var deadline = new CancellationTokenSource(_settings.TotalTimeout * 3, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token))
        {
            try
            {
                result = await _provider.PollAsync(account, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down or lease lost: the next leader polls it
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                result = new PollResult.Failed("poll deadline exceeded");
            }
            catch (Exception ex)
            {
                LogPollThrew(_provider.ProviderId, account.ExternalAccountId, ex.GetType().Name);
                result = new PollResult.Failed($"poll threw {ex.GetType().Name}");
            }
        }

        if (result is PollResult.Completed completed)
        {
            try
            {
                await _recorder.RecordAsync(_provider, account, _settings.PollInterval, completed, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRecordFailed(_provider.ProviderId, account.ExternalAccountId, ex.GetType().Name);
            }
        }

        var (next, pausedUntil) = Schedule(started, _settings.PollInterval, result);
        if (result is PollResult.Failed failed)
        {
            LogPollFailed(_provider.ProviderId, account.ExternalAccountId, failed.Reason, next);
        }

        try
        {
            await _schedule.RecordPollAsync(account, next, pausedUntil, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScheduleFailed(_provider.ProviderId, account.ExternalAccountId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing due {ProviderId} accounts failed ({Error}); trying again next tick")]
    private partial void LogListFailed(string providerId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling {ProviderId} account {ExternalAccountId} threw {Error}; scheduled as a failed poll")]
    private partial void LogPollThrew(string providerId, string externalAccountId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling {ProviderId} account {ExternalAccountId} failed: {Reason}; next poll at {NextPollAt:O}")]
    private partial void LogPollFailed(string providerId, string externalAccountId, string reason, DateTimeOffset nextPollAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a {ProviderId} poll of account {ExternalAccountId} failed ({Error}); the next poll reports again")]
    private partial void LogRecordFailed(string providerId, string externalAccountId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the schedule of {ProviderId} account {ExternalAccountId} failed ({Error})")]
    private partial void LogScheduleFailed(string providerId, string externalAccountId, string error);
}
