using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>One or more normalised readings arrived for a device (from the gateway or a poll).</summary>
public sealed record TelemetryReceived : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(TelemetryReceived);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the readings were received (UTC).</param>
    /// <param name="traceparent">The W3C trace context, if any.</param>
    /// <param name="deviceId">The device the readings belong to.</param>
    /// <param name="providerId">The provider that produced them.</param>
    /// <param name="readings">The readings; at least one.</param>
    public TelemetryReceived(Guid messageId, DateTimeOffset occurredAt, string? traceparent, DeviceId deviceId, string providerId, IReadOnlyList<DeviceReading> readings)
        : base(messageId, occurredAt, traceparent)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0)
        {
            throw new ArgumentException("At least one reading is required.", nameof(readings));
        }

        DeviceId = Require.NotDefault(deviceId);
        ProviderId = Require.NotBlank(providerId);
        Readings = [.. readings];
    }

    /// <inheritdoc/>
    public override string MessageType => TypeName;

    /// <inheritdoc/>
    public override int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the device the readings belong to.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Gets the provider that produced the readings.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the readings.</summary>
    public IReadOnlyList<DeviceReading> Readings { get; }
}
