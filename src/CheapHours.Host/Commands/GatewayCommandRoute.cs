using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.Ocpp;

namespace CheapHours.Host.Commands;

/// <summary>
/// Sends a command to an OCPP charger through the gateway replica that holds its WebSocket (D1, ADR-008): looks
/// the charger up in the connection registry and publishes a <see cref="GatewayCommand"/> to that replica's queue.
/// A charger with no connection, a replica queue that is gone, or a broker failure is transient; the replica's
/// answer arrives later as a <see cref="GatewayCommandResult"/>.
/// </summary>
internal sealed class GatewayCommandRoute(IConnectionRegistry connections, IMessagePublisher publisher, MessagingTopology topology, TimeProvider time) : ICommandRoute
{
    /// <summary>The reason for a charger that is not connected to any replica.</summary>
    public const string NotConnected = "the charger is not connected";

    /// <inheritdoc/>
    public bool Reaches(StoredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Device.ProviderId == OcppProvider.Id && device.Device.Type == DeviceType.Charger;
    }

    /// <inheritdoc/>
    public async Task<RouteOutcome> SendAsync(StoredCommand command, StoredDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(device);
        var chargePointId = device.Device.ExternalId;
        if (await connections.FindAsync(chargePointId, cancellationToken).ConfigureAwait(false) is not { } connection)
        {
            return new RouteOutcome.Completed(new SendResult.Transient(NotConnected));
        }

        var now = time.GetUtcNow();
        var message = new GatewayCommand(Guid.CreateVersion7(now), now, command.Traceparent, chargePointId, connection.ReplicaId, command.Command.Request);
        try
        {
            await publisher.SendToQueueAsync(message, topology.GatewayCommandQueue(connection.ReplicaId), cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            // An unroutable publish means the replica's queue is gone; either way the retry finds the right replica.
            return new RouteOutcome.Completed(new SendResult.Transient($"gateway replica {connection.ReplicaId} could not be reached"));
        }

        return new RouteOutcome.Forwarded();
    }
}
