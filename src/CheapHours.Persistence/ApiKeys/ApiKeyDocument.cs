using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.ApiKeys;

/// <summary>An <c>apiKeys</c> document, keyed by the key's SHA-256 hash (D3).</summary>
internal sealed class ApiKeyDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public List<string> Scopes { get; set; } = [];

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
