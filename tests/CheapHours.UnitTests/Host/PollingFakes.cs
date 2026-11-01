using System.Collections.Concurrent;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;

namespace CheapHours.UnitTests.Host;

/// <summary>A device store that keeps devices by (provider, external id) and logs every call.</summary>
internal sealed class InMemoryDeviceStore : IDeviceStateStore
{
    private readonly Dictionary<(string, string), Device> _devices = [];

    public List<string> Calls { get; } = [];

    public List<(Device Device, TimeSpan Interval, DateTimeOffset At)> Ensured { get; } = [];

    public void Add(Device device) => _devices[(device.ProviderId, device.ExternalId)] = device;

    public Task<DeviceId> EnsureAsync(Device device, TimeSpan reportInterval, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Ensured.Add((device, reportInterval, at));
        Calls.Add($"ensure {device.ExternalId}");
        if (!_devices.TryGetValue((device.ProviderId, device.ExternalId), out var existing))
        {
            _devices[(device.ProviderId, device.ExternalId)] = existing = device;
        }

        return Task.FromResult(existing.Id);
    }

    public Task RecordHardwareAsync(string providerId, string externalId, DeviceHardware hardware, CancellationToken cancellationToken)
    {
        Calls.Add($"hardware {externalId} {hardware.Model}");
        return Task.CompletedTask;
    }

    public Task<bool> RecordStatusAsync(string providerId, string externalId, DeviceStatus status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Calls.Add($"status {externalId} {status}");
        return Task.FromResult(true);
    }

    public Task<bool> MarkOfflineAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> MarkRemovedAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Calls.Add($"removed {externalId}");
        return Task.FromResult(true);
    }

    public Task RecordSeenAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Calls.Add($"seen {externalId} {at:O}");
        return Task.CompletedTask;
    }

    public Task<Device?> FindAsync(string providerId, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(_devices.GetValueOrDefault((providerId, externalId)));
}

/// <summary>A publisher that keeps what was published.</summary>
internal sealed class InMemoryPublisher : IMessagePublisher
{
    public ConcurrentQueue<Message> Published { get; } = new();

    public IReadOnlyList<TelemetryReceived> Telemetry => [.. Published.OfType<TelemetryReceived>()];

    public Task PublishAsync(Message message, CancellationToken cancellationToken)
    {
        Published.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>A poll schedule with the same due rules as the MongoDB store.</summary>
internal sealed class InMemoryPollingSchedule : IPollingSchedule
{
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    public void Add(ProviderAccount account, DateTimeOffset? nextPollAt = null, DateTimeOffset? pausedUntil = null, bool reconnectNeeded = false)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(account) { NextPollAt = nextPollAt, PausedUntil = pausedUntil, ReconnectNeeded = reconnectNeeded });
        }
    }

    public (DateTimeOffset? NextPollAt, DateTimeOffset? PausedUntil) Of(string externalAccountId)
    {
        lock (_lock)
        {
            var entry = _entries.Single(e => e.Account.ExternalAccountId == externalAccountId);
            return (entry.NextPollAt, entry.PausedUntil);
        }
    }

    public Task<IReadOnlyList<ProviderAccount>> ListDueAsync(string providerId, DateTimeOffset now, IReadOnlyCollection<string> exclude, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<ProviderAccount> due =
            [
                .. _entries
                    .Where(e => e.Account.ProviderId == providerId && !e.ReconnectNeeded
                        && (e.NextPollAt is null || e.NextPollAt <= now)
                        && (e.PausedUntil is null || e.PausedUntil <= now)
                        && !exclude.Contains(e.Account.ExternalAccountId))
                    .OrderBy(e => e.NextPollAt ?? DateTimeOffset.MinValue)
                    .Take(limit)
                    .Select(e => e.Account),
            ];
            return Task.FromResult(due);
        }
    }

    public Task RecordPollAsync(ProviderAccount account, DateTimeOffset nextPollAt, DateTimeOffset? pausedUntil, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var entry = _entries.Single(e => e.Account == account);
            entry.NextPollAt = nextPollAt;
            entry.PausedUntil = pausedUntil;
        }

        return Task.CompletedTask;
    }

    private sealed class Entry(ProviderAccount account)
    {
        public ProviderAccount Account { get; } = account;

        public DateTimeOffset? NextPollAt { get; set; }

        public DateTimeOffset? PausedUntil { get; set; }

        public bool ReconnectNeeded { get; set; }
    }
}

/// <summary>A polling provider whose answers the test scripts; it counts polls and can hold them open.</summary>
internal sealed class ScriptedPollingProvider : IPollingProvider
{
    private int _polls;

    public string ProviderId => "scripted";

    public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.ReadStateOfCharge | Capabilities.StartStop | Capabilities.PushesEvents;

    public int Polls => Volatile.Read(ref _polls);

    public ConcurrentQueue<(string Account, DateTimeOffset At)> Log { get; } = new();

    /// <summary>Gets or sets the answer; may throw or wait.</summary>
    public Func<ProviderAccount, CancellationToken, Task<PollResult>> Answer { get; set; } =
        (_, _) => Task.FromResult<PollResult>(new PollResult.Completed([]));

    public TimeProvider? Clock { get; set; }

    public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _polls);
        Log.Enqueue((account.ExternalAccountId, Clock?.GetUtcNow() ?? default));
        return Answer(account, cancellationToken);
    }
}

/// <summary>A poll recorder that counts what it was given.</summary>
internal sealed class CountingPollRecorder : CheapHours.Host.Polling.IPollRecorder
{
    public ConcurrentQueue<string> Recorded { get; } = new();

    public Task RecordAsync(IPollingProvider provider, ProviderAccount account, TimeSpan pollInterval, PollResult.Completed result, CancellationToken cancellationToken)
    {
        Recorded.Enqueue(account.ExternalAccountId);
        return Task.CompletedTask;
    }
}
