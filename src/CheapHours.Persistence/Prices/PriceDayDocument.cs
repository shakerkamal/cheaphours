using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Prices;

/// <summary>
/// A <c>priceDays</c> document: the import's progress for one zone and Berlin day, so "announce once" and
/// "warn once" survive restarts.
/// </summary>
internal sealed class PriceDayDocument
{
    /// <summary>Gets or sets the id: <c>{zone}|{yyyy-MM-dd}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Zone { get; set; } = string.Empty;

    /// <summary>Gets or sets the Berlin day as <c>yyyy-MM-dd</c>.</summary>
    public string Day { get; set; } = string.Empty;

    public int SlotCount { get; set; }

    /// <summary>Gets or sets when the day was first fetched, whatever the outcome.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? AttemptedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? CompleteAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? AnnouncedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? GaveUpAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
