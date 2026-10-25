namespace CheapHours.Persistence.Connections;

/// <summary>The connection registry (<c>connections</c>): charge point id → gateway replica (D1, ADR-008).</summary>
public interface IConnectionRegistry
{
    /// <summary>Records a new connection, replacing any earlier entry for the same charger.</summary>
    /// <param name="entry">The connection.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when stored.</returns>
    Task RecordAsync(ConnectionEntry entry, CancellationToken cancellationToken);

    /// <summary>Removes a charger's entry only if it still holds <paramref name="connectionId"/>.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <param name="connectionId">The connection that ended.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    Task<bool> RemoveIfCurrentAsync(string chargePointId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Removes every entry held by a replica (used when it shuts down).</summary>
    /// <param name="replicaId">The replica id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many entries were removed.</returns>
    Task<long> RemoveReplicaAsync(string replicaId, CancellationToken cancellationToken);

    /// <summary>Reads a charger's entry.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entry, or <see langword="null"/> when the charger is not connected anywhere.</returns>
    Task<ConnectionEntry?> FindAsync(string chargePointId, CancellationToken cancellationToken);
}
