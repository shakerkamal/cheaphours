using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Connections;

/// <summary>A <c>connections</c> document: which gateway replica holds a charger's WebSocket (D1, ADR-008).</summary>
internal sealed class ConnectionDocument
{
    /// <summary>Gets or sets the charge point id; the same as the registration's <c>_id</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string ReplicaId { get; set; } = string.Empty;

    public string ConnectionId { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ConnectedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
