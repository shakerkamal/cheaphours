using CheapHours.Core.Commands;
using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Commands;

/// <summary>
/// A <c>commands</c> document. <see cref="State"/> and <see cref="Attempts"/> are stored for the indexed scans;
/// reading replays <see cref="History"/> through Core, which must agree with them. Every write increments
/// <see cref="Version"/>.
/// </summary>
internal sealed class CommandDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public CommandType Type { get; set; }

    [BsonIgnoreIfNull]
    public int? MaxPowerW { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime DueAt { get; set; }

    [BsonIgnoreIfNull]
    public string? PlanId { get; set; }

    [BsonIgnoreIfNull]
    public int? PlanVersion { get; set; }

    [BsonIgnoreIfNull]
    public string? Traceparent { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public CommandState State { get; set; }

    public int Attempts { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? NextAttemptAt { get; set; }

    public List<HistoryEntryDocument> History { get; set; } = [];

    [BsonIgnoreIfNull]
    public ClaimDocument? Claim { get; set; }

    public int Announcements { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? LastAnnouncedAt { get; set; }

    public bool AlreadyInEffect { get; set; }

    public long Version { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>One state the command entered.</summary>
    internal sealed class HistoryEntryDocument
    {
        public CommandState State { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime At { get; set; }

        [BsonIgnoreIfNull]
        public string? VendorRef { get; set; }

        [BsonIgnoreIfNull]
        public string? Reason { get; set; }
    }

    /// <summary>A dispatcher's hold on the command.</summary>
    internal sealed class ClaimDocument
    {
        public string By { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime Until { get; set; }
    }
}
