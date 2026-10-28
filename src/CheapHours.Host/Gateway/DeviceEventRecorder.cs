using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Devices;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Records the normalised events of one accepted frame, the same way for any provider: hardware and
/// status go to the device store, all readings of the frame go out as one confirmed
/// <see cref="TelemetryReceived"/>, malformed input is logged by its fixed reason. Store writes come first
/// and the publish last; the caller answers the device only after both succeeded.
/// </summary>
internal sealed partial class DeviceEventRecorder(IDeviceStateStore devices, IMessagePublisher publisher, ILogger<DeviceEventRecorder> logger)
{
    /// <summary>Records <paramref name="events"/>.</summary>
    /// <param name="providerId">The provider that reported them.</param>
    /// <param name="externalId">The vendor's id of the device.</param>
    /// <param name="deviceId">The device's id.</param>
    /// <param name="events">The events, in frame order.</param>
    /// <param name="receivedAt">When the frame was received (UTC); the message's occurrence time.</param>
    /// <param name="cancellationToken">Cancels the recording.</param>
    /// <returns>A task that completes when everything is stored and published.</returns>
    /// <exception cref="BrokerUnavailableException">The readings could not be published.</exception>
    public async Task RecordAsync(string providerId, string externalId, DeviceId deviceId, IReadOnlyList<DeviceEvent> events, DateTimeOffset receivedAt, CancellationToken cancellationToken)
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
                    await devices.RecordStatusAsync(providerId, externalId, status.Status, status.At, cancellationToken).ConfigureAwait(false);
                    break;
                case DeviceEvent.ReadingReported reported:
                    readings.Add(reported.Reading);
                    break;
                case DeviceEvent.MalformedInput malformed:
                    LogMalformed(providerId, externalId, malformed.Reason);
                    break;
            }
        }

        if (readings.Count > 0)
        {
            var message = new TelemetryReceived(Guid.CreateVersion7(receivedAt), receivedAt, traceparent: null, deviceId, providerId, readings);
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed input from {ProviderId} device {ExternalId}: {Reason}")]
    private partial void LogMalformed(string providerId, string externalId, string reason);
}
