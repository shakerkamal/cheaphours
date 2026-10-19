using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>A command was saved and must be dispatched (published by the outbox relay).</summary>
public sealed record CommandRequested : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(CommandRequested);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the command was saved (UTC).</param>
    /// <param name="traceparent">The W3C trace context of the API call that created it.</param>
    /// <param name="commandId">The command.</param>
    /// <param name="deviceId">The device that must act.</param>
    /// <param name="idempotencyKey">The command's idempotency key.</param>
    public CommandRequested(Guid messageId, DateTimeOffset occurredAt, string? traceparent, CommandId commandId, DeviceId deviceId, string idempotencyKey)
        : base(messageId, occurredAt, traceparent)
    {
        CommandId = Require.NotDefault(commandId);
        DeviceId = Require.NotDefault(deviceId);
        IdempotencyKey = Require.NotBlank(idempotencyKey);
    }

    /// <inheritdoc/>
    public override string MessageType => TypeName;

    /// <inheritdoc/>
    public override int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the command.</summary>
    public CommandId CommandId { get; }

    /// <summary>Gets the device that must act.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Gets the command's idempotency key.</summary>
    public string IdempotencyKey { get; }
}
