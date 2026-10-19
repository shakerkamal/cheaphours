namespace CheapHours.Messaging.Contracts;

/// <summary>
/// The envelope every broker message shares. Adding an optional field keeps the schema version; removing
/// or retyping one needs a new version.
/// </summary>
public abstract record Message
{
    /// <summary>Creates the envelope.</summary>
    /// <param name="messageId">A unique id for this message, used by consumers' inbox for de-duplication.</param>
    /// <param name="occurredAt">When the fact happened, in UTC.</param>
    /// <param name="traceparent">The W3C trace context; absent only for messages started by a scheduled job.</param>
    /// <exception cref="ArgumentException">The id is empty or the time is not UTC.</exception>
    protected Message(Guid messageId, DateTimeOffset occurredAt, string? traceparent)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message needs a non-empty id.", nameof(messageId));
        }

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Must be UTC (offset 00:00), but the offset is {occurredAt.Offset}.", nameof(occurredAt));
        }

        MessageId = messageId;
        OccurredAt = occurredAt;
        Traceparent = traceparent;
    }

    /// <summary>Gets the message's unique id.</summary>
    public Guid MessageId { get; }

    /// <summary>Gets when the fact happened (UTC).</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Gets the message type name, for example <c>CommandRequested</c>.</summary>
    public abstract string MessageType { get; }

    /// <summary>Gets the schema version of this message type, 1 or more.</summary>
    public abstract int SchemaVersion { get; }

    /// <summary>Gets the W3C <c>traceparent</c>, if any.</summary>
    public string? Traceparent { get; }
}
