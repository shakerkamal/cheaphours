namespace CheapHours.Messaging.Contracts;

/// <summary>A delivery day's prices are complete for a bidding zone; open goals should re-plan.</summary>
public sealed record PricesPublished : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(PricesPublished);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the import completed (UTC).</param>
    /// <param name="traceparent">The W3C trace context; usually absent, since a scheduled job starts it.</param>
    /// <param name="zone">The bidding zone, for example <c>DE-LU</c>.</param>
    /// <param name="deliveryDay">The delivery day in the zone's local calendar.</param>
    /// <param name="slotCount">How many 15-minute slots the day has (96, or 92/100 on daylight-saving days).</param>
    public PricesPublished(Guid messageId, DateTimeOffset occurredAt, string? traceparent, string zone, DateOnly deliveryDay, int slotCount)
        : base(messageId, occurredAt, traceparent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);

        Zone = Require.NotBlank(zone);
        DeliveryDay = deliveryDay;
        SlotCount = slotCount;
    }

    /// <inheritdoc/>
    public override string MessageType => TypeName;

    /// <inheritdoc/>
    public override int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the bidding zone.</summary>
    public string Zone { get; }

    /// <summary>Gets the delivery day.</summary>
    public DateOnly DeliveryDay { get; }

    /// <summary>Gets the number of slots imported.</summary>
    public int SlotCount { get; }
}
