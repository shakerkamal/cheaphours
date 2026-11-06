using CheapHours.Core.Goals;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Plans;

/// <summary>
/// A <c>plans</c> document: one version of a goal's plan. The id <c>{planId}:v{version}</c> makes each version
/// unique, so two re-plans that both try to store version N leave exactly one.
/// </summary>
internal sealed class PlanDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public int Version { get; set; }

    public string GoalId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public PlanStatus Status { get; set; }

    public PlanReason Reason { get; set; }

    public List<SlotDocument> Slots { get; set; } = [];

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal ExpectedCostEur { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal ArrivalCostEur { get; set; }

    public bool TargetMissed { get; set; }

    public bool PricesComplete { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal StartSocPercent { get; set; }

    public int TargetSocPercent { get; set; }

    public long EnergyNeededWh { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime MadeAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Gets or sets when the owner confirmed this plan; copied to versions re-planned from it.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? ConfirmedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>One chosen slot.</summary>
    internal sealed class SlotDocument
    {
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime Start { get; set; }

        public int PowerW { get; set; }
    }
}
