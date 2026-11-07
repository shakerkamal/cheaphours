using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Outbox;

/// <summary>
/// A message saved in the same transaction as the change it announces, waiting for the relay to publish it.
/// The body is the message's JSON; the envelope fields travel next to it so the relay can rebuild the broker
/// properties without knowing the message type.
/// </summary>
/// <param name="MessageId">The message's id; also the entry's id.</param>
/// <param name="Type">The message type name, for example <c>CommandRequested</c>.</param>
/// <param name="SchemaVersion">The message's schema version.</param>
/// <param name="Body">The message serialised as JSON.</param>
/// <param name="Traceparent">The W3C trace context, if any.</param>
/// <param name="Route">The exchange the relay publishes to.</param>
/// <param name="Destination">The routing key on the events exchange, or the queue name for the default exchange.</param>
/// <param name="AvailableAt">The earliest time the relay may publish it (UTC).</param>
/// <param name="CreatedAt">When it was saved (UTC); the message's occurrence time.</param>
/// <param name="CommandId">The command it announces, if any.</param>
/// <param name="PublishedAt">When the broker confirmed it, or <see langword="null"/> while it waits.</param>
/// <param name="Attempts">How many publishes failed.</param>
public sealed record OutboxEntry(
    Guid MessageId,
    string Type,
    int SchemaVersion,
    string Body,
    string? Traceparent,
    OutboxRoute Route,
    string Destination,
    DateTimeOffset AvailableAt,
    DateTimeOffset CreatedAt,
    CommandId? CommandId = null,
    DateTimeOffset? PublishedAt = null,
    int Attempts = 0);
