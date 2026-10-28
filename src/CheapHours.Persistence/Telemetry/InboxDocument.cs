using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Telemetry;

/// <summary>A message a consumer has handled; expires after <see cref="MongoIndexes.InboxRetention"/>.</summary>
internal sealed class InboxDocument
{
    /// <summary>Gets or sets <c>{consumer}:{messageId}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime HandledAt { get; set; }
}
