using CheapHours.Core.Devices;
using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Devices;

/// <summary>A <c>devices</c> document. Capabilities are stored as names, so documents stay readable and independent of bit values.</summary>
internal sealed class DeviceDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    public DeviceType Type { get; set; }

    public List<string> Capabilities { get; set; } = [];

    public AttributesDocument Attributes { get; set; } = new();

    [BsonIgnoreIfNull]
    public HardwareDocument? Hardware { get; set; }

    public DeviceStatus Status { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime StatusAt { get; set; }

    /// <summary>Gets or sets the time of the last status the device itself reported (not one the gateway set).</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? ReportedStatusAt { get; set; }

    /// <summary>Gets or sets when CheapHours received the latest reported status: commands are confirmed against this receipt time, never the device's own clock.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? StatusReceivedAt { get; set; }

    /// <summary>Gets or sets the device's current charging session as its vendor names it (an OCPP transaction id); absent when none runs.</summary>
    [BsonIgnoreIfNull]
    public string? ActiveSessionId { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Gets or sets how often the device is expected to report, in seconds; absent when unknown.</summary>
    [BsonIgnoreIfNull]
    public int? ReportIntervalSeconds { get; set; }

    /// <summary>Gets or sets the latest reading. The worker writes it with a pipeline update (see <c>LastReadingUpdate</c>).</summary>
    [BsonIgnoreIfNull]
    public LastReadingDocument? LastReading { get; set; }

    /// <summary>Gets or sets when the vendor reported the device as removed; cleared when it is discovered again.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? RemovedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>The latest reading; power may be derived (<see cref="PowerDerived"/>), and unknown values are null.</summary>
    internal sealed class LastReadingDocument
    {
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime Ts { get; set; }

        public long EnergyWh { get; set; }

        public int? PowerW { get; set; }

        public double? SocPercent { get; set; }

        public bool? PowerDerived { get; set; }
    }

    /// <summary>Typed attributes (D2).</summary>
    internal sealed class AttributesDocument
    {
        [BsonIgnoreIfNull]
        public int? MaxPowerW { get; set; }

        [BsonIgnoreIfNull]
        public long? BatteryCapacityWh { get; set; }
    }

    /// <summary>What the device reported about itself.</summary>
    internal sealed class HardwareDocument
    {
        public string Manufacturer { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        [BsonIgnoreIfNull]
        public string? FirmwareVersion { get; set; }
    }
}
