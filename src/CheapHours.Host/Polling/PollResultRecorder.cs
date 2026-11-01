using System.Globalization;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Devices;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.Host.Polling;

/// <summary>
/// Records a completed poll (design §6). Events are grouped by the vendor's device id, keeping their order. A
/// <see cref="DeviceEvent.Discovered"/> device joins the polled account's owner (or keeps its id when it exists),
/// with the provider's capabilities and the poll interval as its report interval. Each device's events then go
/// through the shared <see cref="DeviceEventRecorder"/> with last-seen taken from the vendor's own timestamps and a
/// message id derived from the readings, so a repeated poll adds no duplicate reading.
/// </summary>
internal sealed partial class PollResultRecorder(IDeviceStateStore devices, DeviceEventRecorder recorder, TimeProvider time, ILogger<PollResultRecorder> logger)
    : IPollRecorder
{
    /// <inheritdoc/>
    public async Task RecordAsync(IPollingProvider provider, ProviderAccount account, TimeSpan pollInterval, PollResult.Completed result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(result);

        var polledAt = time.GetUtcNow();
        foreach (var (externalId, events) in Group(provider.ProviderId, result.Events))
        {
            var deviceId = await DeviceIdAsync(provider, account, pollInterval, externalId, events, cancellationToken).ConfigureAwait(false);
            if (deviceId is null)
            {
                continue;
            }

            var readingTimes = events.OfType<DeviceEvent.ReadingReported>().Select(r => r.Reading.MeasurementTime.ToString("O", CultureInfo.InvariantCulture));
            var messageId = DeterministicGuid.From($"{provider.ProviderId}\n{externalId}\n{string.Join('\n', readingTimes)}");
            var seen = events.Where(e => e is not DeviceEvent.DeviceRemoved).Select(e => (DateTimeOffset?)e.At).Max();

            await recorder.RecordAsync(provider.ProviderId, externalId, deviceId.Value, events, polledAt, messageId, seen, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ExternalIdOf(DeviceEvent deviceEvent) => deviceEvent switch
    {
        DeviceEvent.Discovered e => e.ExternalId,
        DeviceEvent.Connected e => e.ExternalId,
        DeviceEvent.StatusChanged e => e.ExternalId,
        DeviceEvent.Disconnected e => e.ExternalId,
        DeviceEvent.DeviceRemoved e => e.ExternalId,
        DeviceEvent.ReadingReported e => e.Reading.ExternalId,
        _ => null,
    };

    private List<(string ExternalId, List<DeviceEvent> Events)> Group(string providerId, IReadOnlyList<DeviceEvent> events)
    {
        var groups = new List<(string, List<DeviceEvent>)>();
        var index = new Dictionary<string, List<DeviceEvent>>(StringComparer.Ordinal);
        foreach (var deviceEvent in events)
        {
            if (ExternalIdOf(deviceEvent) is not { } externalId)
            {
                if (deviceEvent is DeviceEvent.MalformedInput malformed)
                {
                    LogMalformed(providerId, malformed.Reason);
                }

                continue;
            }

            if (!index.TryGetValue(externalId, out var group))
            {
                group = [];
                index[externalId] = group;
                groups.Add((externalId, group));
            }

            group.Add(deviceEvent);
        }

        return groups;
    }

    private async Task<DeviceId?> DeviceIdAsync(IPollingProvider provider, ProviderAccount account, TimeSpan pollInterval, string externalId, List<DeviceEvent> events, CancellationToken cancellationToken)
    {
        if (events.OfType<DeviceEvent.Discovered>().LastOrDefault() is { } discovered)
        {
            var device = new Device(
                DeviceId.New(time),
                account.OwnerId,
                provider.ProviderId,
                externalId,
                discovered.Type,
                provider.Capabilities & ~Capabilities.PushesEvents,
                discovered.Attributes);
            return await devices.EnsureAsync(device, pollInterval, discovered.At, cancellationToken).ConfigureAwait(false);
        }

        if (await devices.FindAsync(provider.ProviderId, externalId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing.Id;
        }

        if (events.TrueForAll(e => e is DeviceEvent.DeviceRemoved))
        {
            LogRemovedUnknown(provider.ProviderId, externalId);
        }
        else
        {
            LogUnknown(provider.ProviderId, externalId);
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed input from {ProviderId}: {Reason}")]
    private partial void LogMalformed(string providerId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{ProviderId} reported events for device {ExternalId}, which was never discovered; skipped")]
    private partial void LogUnknown(string providerId, string externalId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ProviderId} reports device {ExternalId} as removed, but it was never stored")]
    private partial void LogRemovedUnknown(string providerId, string externalId);
}
