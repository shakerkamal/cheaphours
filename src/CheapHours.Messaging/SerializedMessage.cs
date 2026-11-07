using System.Text.Json;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;

namespace CheapHours.Messaging;

/// <summary>
/// A message turned into its JSON body and envelope fields, so it can be stored (an outbox entry) and published
/// later exactly as it was built, without knowing its type.
/// </summary>
/// <param name="MessageId">The message's unique id.</param>
/// <param name="Type">The message type name.</param>
/// <param name="SchemaVersion">The message's schema version.</param>
/// <param name="OccurredAt">When the fact happened (UTC).</param>
/// <param name="Traceparent">The W3C trace context, if any.</param>
/// <param name="Json">The message serialised with the shared JSON options.</param>
public sealed record SerializedMessage(Guid MessageId, string Type, int SchemaVersion, DateTimeOffset OccurredAt, string? Traceparent, string Json)
{
    /// <summary>Serialises <paramref name="message"/> as the publisher would.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The serialised message.</returns>
    public static SerializedMessage From(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(
            message.MessageId,
            message.MessageType,
            message.SchemaVersion,
            message.OccurredAt,
            message.Traceparent,
            JsonSerializer.Serialize(message, message.GetType(), CheapHoursJson.Options));
    }
}
