using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.ChargePoints;

/// <summary>A <c>chargePoints</c> document: one registered charger and its basic-auth password hash.</summary>
internal sealed class ChargePointDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public int MaxPowerW { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? RevokedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
