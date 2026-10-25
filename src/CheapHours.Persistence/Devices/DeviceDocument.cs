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

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? LastSeenAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;

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
