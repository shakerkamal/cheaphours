using System.Globalization;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Devices;

namespace CheapHours.Host.Devices;

/// <summary>
/// Records events a vendor cloud reported for many devices at once, polled or pushed (design §3). Events are
/// grouped by the vendor's device id, keeping their order. A <see cref="DeviceEvent.Discovered"/> device joins the
/// given owner (or keeps its id when it exists), with the provider's capabilities and the report interval. A device
/// with no record and no discovery is skipped with a warning. Each device's events then go through the shared
/// <see cref="DeviceEventRecorder"/> with last-seen taken from the vendor's own timestamps and a message id derived
/// from the provider, the vendor id and the reading times, so a repeated poll or a redelivered webhook adds no
/// duplicate reading.
/// </summary>
internal sealed partial class DeviceEventIngestor(IDeviceStateStore devices, DeviceEventRecorder recorder, TimeProvider time, ILogger<DeviceEventIngestor> logger)
{
    /// <summary>Records <paramref name="events"/>.</summary>
    /// <param name="providerId">The provider that reported them.</param>
    /// <param name="capabilities">The provider's declared capabilities; discovered devices get them without <see cref="Capabilities.PushesEvents"/>.</param>
    /// <param name="owner">The owner of the account the events came through; discovered devices join it.</param>
    /// <param name="reportInterval">How often the vendor reports each device; the report interval of discovered devices.</param>
    /// <param name="events">The events, in the order observed.</param>
    /// <param name="occurredAt">When they were polled or received (UTC); the telemetry messages' occurrence time.</param>
    /// <param name="cancellationToken">Cancels the recording.</param>
    /// <returns>A task that completes when every device's events are stored and published.</returns>
    /// <exception cref="CheapHours.Messaging.BrokerUnavailableException">Readings could not be published.</exception>
    public async Task IngestAsync(
        string providerId,
        Capabilities capabilities,
        OwnerId owner,
        TimeSpan reportInterval,
        IReadOnlyList<DeviceEvent> events,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(events);

        foreach (var (externalId, group) in Group(providerId, events))
        {
            var deviceId = await DeviceIdAsync(providerId, capabilities, owner, reportInterval, externalId, group, cancellationToken).ConfigureAwait(false);
            if (deviceId is null)
            {
                continue;
            }

            var readingTimes = group.OfType<DeviceEvent.ReadingReported>().Select(r => r.Reading.MeasurementTime.ToString("O", CultureInfo.InvariantCulture));
            var messageId = DeterministicGuid.From($"{providerId}\n{externalId}\n{string.Join('\n', readingTimes)}");
            var seen = group.Where(e => e is not DeviceEvent.DeviceRemoved).Select(e => (DateTimeOffset?)e.At).Max();

            await recorder.RecordAsync(providerId, externalId, deviceId.Value, group, occurredAt, messageId, seen, cancellationToken).ConfigureAwait(false);
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

    private async Task<DeviceId?> DeviceIdAsync(
        string providerId,
        Capabilities capabilities,
        OwnerId owner,
        TimeSpan reportInterval,
        string externalId,
        List<DeviceEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.OfType<DeviceEvent.Discovered>().LastOrDefault() is { } discovered)
        {
            var device = new Device(
                DeviceId.New(time),
                owner,
                providerId,
                externalId,
                discovered.Type,
                capabilities & ~Capabilities.PushesEvents,
                discovered.Attributes);
            return await devices.EnsureAsync(device, reportInterval, discovered.At, cancellationToken).ConfigureAwait(false);
        }

        if (await devices.FindAsync(providerId, externalId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing.Id;
        }

        if (events.TrueForAll(e => e is DeviceEvent.DeviceRemoved))
        {
            LogRemovedUnknown(providerId, externalId);
        }
        else
        {
            LogUnknown(providerId, externalId);
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
