using System.Globalization;
using MongoDB.Driver;

namespace CheapHours.Persistence.Connections;

/// <summary>MongoDB implementation of <see cref="IConnectionRegistry"/>.</summary>
/// <param name="database">The database.</param>
public sealed class ConnectionRegistry(IMongoDatabase database) : IConnectionRegistry
{
    // Connection ids are stored as 32 hex characters, avoiding the driver's GUID representation settings.
    private const string GuidFormat = "N";

    private readonly IMongoCollection<ConnectionDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public Task RecordAsync(ConnectionEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var document = new ConnectionDocument
        {
            Id = entry.ChargePointId,
            ReplicaId = entry.ReplicaId,
            ConnectionId = entry.ConnectionId.ToString(GuidFormat, CultureInfo.InvariantCulture),
            ConnectedAt = UtcTime.ToBson(entry.ConnectedAt),
        };

        return _collection.ReplaceOneAsync(d => d.Id == entry.ChargePointId, document, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveIfCurrentAsync(string chargePointId, Guid connectionId, CancellationToken cancellationToken)
    {
        var id = connectionId.ToString(GuidFormat, CultureInfo.InvariantCulture);
        var result = await _collection.DeleteOneAsync(d => d.Id == chargePointId && d.ConnectionId == id, cancellationToken).ConfigureAwait(false);
        return result.DeletedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<long> RemoveReplicaAsync(string replicaId, CancellationToken cancellationToken)
    {
        var result = await _collection.DeleteManyAsync(d => d.ReplicaId == replicaId, cancellationToken).ConfigureAwait(false);
        return result.DeletedCount;
    }

    /// <inheritdoc/>
    public async Task<ConnectionEntry?> FindAsync(string chargePointId, CancellationToken cancellationToken)
    {
        var d = await _collection.Find(x => x.Id == chargePointId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return d is null
            ? null
            : new ConnectionEntry(d.Id, d.ReplicaId, Guid.ParseExact(d.ConnectionId, GuidFormat), UtcTime.FromBson(d.ConnectedAt));
    }

    private static IMongoCollection<ConnectionDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<ConnectionDocument>(MongoIndexes.Connections);
    }
}
