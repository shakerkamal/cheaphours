using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Prices;

/// <summary>A <c>prices</c> document: one zone's price for one 15-minute slot.</summary>
internal sealed class PriceDocument
{
    /// <summary>Gets or sets the id: <c>{zone}|{start:O}</c>, for example <c>DE-LU|2026-11-02T03:00:00Z</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Zone { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Start { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal PriceEurPerMwh { get; set; }

    /// <summary>Gets or sets when the slot was first imported; a re-import leaves it unchanged.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ImportedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
