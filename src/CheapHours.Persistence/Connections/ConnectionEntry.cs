namespace CheapHours.Persistence.Connections;

/// <summary>A connection registry entry: the replica holding a charger's WebSocket (D1, ADR-008).</summary>
/// <param name="ChargePointId">The charge point id.</param>
/// <param name="ReplicaId">The gateway replica holding the socket.</param>
/// <param name="ConnectionId">Identifies this particular connection, so a late close cannot erase a newer one.</param>
/// <param name="ConnectedAt">When the connection was accepted (UTC).</param>
public sealed record ConnectionEntry(string ChargePointId, string ReplicaId, Guid ConnectionId, DateTimeOffset ConnectedAt);
