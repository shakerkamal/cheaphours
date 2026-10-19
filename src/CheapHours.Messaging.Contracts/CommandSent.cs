using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>A provider accepted a command; the device has not confirmed it yet.</summary>
public sealed record CommandSent : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(CommandSent);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the provider accepted it (UTC).</param>
    /// <param name="traceparent">The W3C trace context, if any.</param>
    /// <param name="commandId">The command.</param>
    /// <param name="deviceId">The device that must act.</param>
    /// <param name="idempotencyKey">The command's idempotency key.</param>
    /// <param name="vendorReference">The vendor's reference for the request, if it gave one.</param>
    public CommandSent(Guid messageId, DateTimeOffset occurredAt, string? traceparent, CommandId commandId, DeviceId deviceId, string idempotencyKey, string? vendorReference = null)
        : base(messageId, occurredAt, traceparent)
    {
        CommandId = Require.NotDefault(commandId);
        DeviceId = Require.NotDefault(deviceId);
        IdempotencyKey = Require.NotBlank(idempotencyKey);
        VendorReference = vendorReference;
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

    /// <summary>Gets the vendor's reference, if any.</summary>
    public string? VendorReference { get; }
}
