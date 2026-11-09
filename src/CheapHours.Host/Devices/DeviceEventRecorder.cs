using System.Diagnostics;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Devices;

namespace CheapHours.Host.Devices;

/// <summary>
/// Records one device's normalised events the same way for any provider and any role: hardware and status go to
/// the device store, a removal marks the device, all readings go out as one confirmed
/// <see cref="TelemetryReceived"/>, and malformed input is logged by its fixed reason. Store writes come first
/// and the publish last; the gateway answers the device only after both succeeded. <see cref="DeviceEvent.Discovered"/>
/// is handled by the poll layer before this, and <see cref="DeviceEvent.Disconnected"/> is ignored.
/// </summary>
internal sealed partial class DeviceEventRecorder(IDeviceStateStore devices, IMessagePublisher publisher, ILogger<DeviceEventRecorder> logger)
{
    /// <summary>Records <paramref name="events"/>.</summary>
    /// <param name="providerId">The provider that reported them.</param>
    /// <param name="externalId">The vendor's id of the device.</param>
    /// <param name="deviceId">The device's id.</param>
    /// <param name="events">The device's events, in the order observed.</param>
    /// <param name="occurredAt">When they were received or polled (UTC); the message's occurrence time.</param>
    /// <param name="messageId">
    /// The telemetry message's id: a version-7 id for a gateway frame; for a poll, one derived from the readings,
    /// so a repeated poll is dropped by the inbox.
    /// </param>
    /// <param name="seenAt">When the device was last seen, recorded before the publish; <see langword="null"/> when the caller records it itself.</param>
    /// <param name="cancellationToken">Cancels the recording.</param>
    /// <returns>A task that completes when everything is stored and published.</returns>
    /// <exception cref="BrokerUnavailableException">The readings could not be published.</exception>
    public async Task RecordAsync(
        string providerId,
        string externalId,
        DeviceId deviceId,
        IReadOnlyList<DeviceEvent> events,
        DateTimeOffset occurredAt,
        Guid messageId,
        DateTimeOffset? seenAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var readings = new List<DeviceReading>();
        foreach (var deviceEvent in events)
        {
            switch (deviceEvent)
            {
                case DeviceEvent.Connected { Hardware: { } hardware }:
                    await devices.RecordHardwareAsync(providerId, externalId, hardware, cancellationToken).ConfigureAwait(false);
                    break;
                case DeviceEvent.StatusChanged status:
                    await devices.RecordStatusAsync(providerId, externalId, status.Status, status.At, occurredAt, cancellationToken).ConfigureAwait(false);
                    break;
                case DeviceEvent.DeviceRemoved removed:
                    await devices.MarkRemovedAsync(providerId, externalId, removed.At, cancellationToken).ConfigureAwait(false);
                    LogRemoved(providerId, externalId);
                    break;
                case DeviceEvent.ReadingReported reported:
                    readings.Add(reported.Reading);
                    break;
                case DeviceEvent.MalformedInput malformed:
                    LogMalformed(providerId, externalId, malformed.Reason);
                    break;
            }
        }

        if (seenAt is { } seen)
        {
            await devices.RecordSeenAsync(providerId, externalId, seen, cancellationToken).ConfigureAwait(false);
        }

        if (readings.Count > 0)
        {
            // The readings travel in the trace of the frame, webhook or poll that brought them.
            var message = new TelemetryReceived(messageId, occurredAt, Activity.Current?.Id, deviceId, providerId, readings);
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed input from {ProviderId} device {ExternalId}: {Reason}")]
    private partial void LogMalformed(string providerId, string externalId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{ProviderId} reports device {ExternalId} as removed; it is marked offline and kept")]
    private partial void LogRemoved(string providerId, string externalId);
}
