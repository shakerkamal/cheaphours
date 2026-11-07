using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Outbox;

/// <summary>
/// An <c>outbox</c> document. <see cref="Published"/> exists beside <see cref="PublishedAt"/> because a partial index
/// can filter on <c>published: false</c> but not on a missing field; the TTL index removes an entry 7 days after
/// <see cref="PublishedAt"/>, and an entry without it never expires.
/// </summary>
internal sealed class OutboxDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int MessageSchemaVersion { get; set; }

    public string Body { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public string? Traceparent { get; set; }

    public OutboxRoute Route { get; set; }

    public string Destination { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AvailableAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public bool Published { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? PublishedAt { get; set; }

    public int Attempts { get; set; }

    [BsonIgnoreIfNull]
    public string? CommandId { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
