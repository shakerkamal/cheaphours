using System.Collections.Concurrent;

namespace CheapHours.Host.Gateway;

/// <summary>The charger connections this replica holds, one per charge point id.</summary>
internal sealed class ConnectionTable
{
    private readonly ConcurrentDictionary<string, ChargerConnection> _byId = new(StringComparer.Ordinal);

    /// <summary>Gets how many connections are open.</summary>
    public int Count => _byId.Count;

    /// <summary>Adds a connection, replacing any older one for the same charger.</summary>
    /// <param name="connection">The new connection.</param>
    /// <returns>The connection it replaced, which the caller must close; <see langword="null"/> when there was none.</returns>
    public ChargerConnection? Add(ChargerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        while (true)
        {
            if (_byId.TryGetValue(connection.ChargePointId, out var previous))
            {
                if (_byId.TryUpdate(connection.ChargePointId, connection, previous))
                {
                    return previous;
                }
            }
            else if (_byId.TryAdd(connection.ChargePointId, connection))
            {
                return null;
            }
        }
    }

    /// <summary>Removes <paramref name="connection"/> if it is still the one held for its charger.</summary>
    /// <param name="connection">The connection that ended.</param>
    /// <returns><see langword="true"/> when it was removed.</returns>
    public bool Remove(ChargerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _byId.TryRemove(KeyValuePair.Create(connection.ChargePointId, connection));
    }

    /// <summary>Finds the connection held for a charger.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <returns>The connection, or <see langword="null"/>.</returns>
    public ChargerConnection? Find(string chargePointId) => _byId.GetValueOrDefault(chargePointId);

    /// <summary>Returns the open connections at this moment.</summary>
    /// <returns>A copy; safe to iterate while connections come and go.</returns>
    public IReadOnlyList<ChargerConnection> Snapshot() => _byId.Values.ToArray();
}
