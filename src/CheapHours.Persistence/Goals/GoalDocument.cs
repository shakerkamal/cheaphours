using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Goals;

/// <summary>A <c>goals</c> document. A goal is open while <see cref="DepartureAt"/> lies in the future.</summary>
internal sealed class GoalDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string VehicleDeviceId { get; set; } = string.Empty;

    public string ChargerDeviceId { get; set; } = string.Empty;

    public int TargetSocPercent { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime DepartureAt { get; set; }

    public string PlanId { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
