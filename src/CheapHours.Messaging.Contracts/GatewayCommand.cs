using System.Text.Json.Serialization;
using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>
/// A command for a charger connected to the gateway, addressed to the replica that holds its WebSocket
/// (D1, ADR-008). The replica sends the OCPP call and answers with a <see cref="GatewayCommandResult"/>.
/// </summary>
public sealed record GatewayCommand : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(GatewayCommand);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the dispatcher created it (UTC).</param>
    /// <param name="traceparent">The W3C trace context, if any.</param>
    /// <param name="chargePointId">The OCPP charge point id (the device's external id).</param>
    /// <param name="replicaId">The gateway replica holding the connection, from the connection registry.</param>
    /// <param name="command">The command to carry out.</param>
    public GatewayCommand(Guid messageId, DateTimeOffset occurredAt, string? traceparent, string chargePointId, string replicaId, DeviceCommand command)
        : base(messageId, occurredAt, traceparent)
    {
        ArgumentNullException.ThrowIfNull(command);

        ChargePointId = Require.NotBlank(chargePointId);
        ReplicaId = Require.NotBlank(replicaId);
        Command = command;
    }

    /// <inheritdoc/>
    public override string MessageType => TypeName;

    /// <inheritdoc/>
    public override int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the OCPP charge point id.</summary>
    public string ChargePointId { get; }

    /// <summary>Gets the gateway replica holding the connection.</summary>
    public string ReplicaId { get; }

    /// <summary>Gets the command to carry out.</summary>
    public DeviceCommand Command { get; }

    /// <summary>Gets the command's id.</summary>
    public CommandId CommandId => Command.Id;

    /// <summary>Gets the command's idempotency key.</summary>
    public string IdempotencyKey => Command.IdempotencyKey;

    /// <summary>Gets the queue this message must be published to: <c>gateway.{replicaId}.commands</c>.</summary>
    [JsonIgnore]
    public string Destination => DestinationFor(ReplicaId);

    /// <summary>Returns the command queue name for a gateway replica.</summary>
    /// <param name="replicaId">The replica id.</param>
    /// <returns><c>gateway.{replicaId}.commands</c>.</returns>
    public static string DestinationFor(string replicaId) => $"gateway.{Require.NotBlank(replicaId)}.commands";
}
