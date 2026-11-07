using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Idempotency;

/// <summary>An <c>idempotencyKeys</c> document: one owner's key, the request it was used for, and its response.</summary>
internal sealed class IdempotencyDocument
{
    /// <summary>Gets or sets the id: <c>{ownerId}|{key}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 of the request's method, route and body.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public IdempotencyState State { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets when the record is forgotten; a TTL index deletes it, and reads treat it as absent from then on.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonIgnoreIfNull]
    public ResponseDocument? Response { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>The stored response.</summary>
    internal sealed class ResponseDocument
    {
        public int Status { get; set; }

        [BsonIgnoreIfNull]
        public string? ContentType { get; set; }

        [BsonIgnoreIfNull]
        public string? Location { get; set; }

        public byte[] Body { get; set; } = [];
    }
}
