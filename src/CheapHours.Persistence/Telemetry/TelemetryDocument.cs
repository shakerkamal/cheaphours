using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Telemetry;

/// <summary>A reading in the <c>telemetry</c> time-series collection. Values not reported are absent, never zero.</summary>
internal sealed class TelemetryDocument
{
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Ts { get; set; }

    public MetaDocument Meta { get; set; } = new();

    public long EnergyWh { get; set; }

    [BsonIgnoreIfNull]
    public int? PowerW { get; set; }

    [BsonIgnoreIfNull]
    public double? SocPercent { get; set; }

    // Lets a repeated message remove what an interrupted earlier attempt wrote (time-series collections
    // have no unique indexes and no transactions).
    public string MessageId { get; set; } = string.Empty;

    internal sealed class MetaDocument
    {
        public string DeviceId { get; set; } = string.Empty;

        public string ProviderId { get; set; } = string.Empty;
    }
}
