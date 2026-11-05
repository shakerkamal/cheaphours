using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Locks;

/// <summary>
/// A lease on <c>locks</c>: one document <c>{ _id: name, holder, expiresAt }</c>. Acquire and renew are one upsert
/// filtered on the lease being expired or already ours; a duplicate-key error means another replica holds it.
/// Release deletes it only when it is ours. Leases with different names never block each other.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="time">The clock.</param>
/// <param name="name">The lease's name (see <see cref="LeaseNames"/>).</param>
public sealed class NamedLease(IMongoDatabase database, TimeProvider time, string name) : ILease
{
    private readonly IMongoCollection<BsonDocument> _locks = (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(MongoIndexes.Locks);

    /// <inheritdoc/>
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A lease needs a name.", nameof(name)) : name;

    /// <inheritdoc/>
    public async Task<bool> TryAcquireAsync(string holder, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var now = time.GetUtcNow().UtcDateTime;

        var filter = new BsonDocument
        {
            { "_id", Name },
            { "$or", new BsonArray { new BsonDocument("expiresAt", new BsonDocument("$lt", now)), new BsonDocument("holder", holder) } },
        };
        var update = new BsonDocument("$set", new BsonDocument { { "holder", holder }, { "expiresAt", now + duration } });
        try
        {
            await _locks.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The filter matched nothing because another holder's lease is live; the upsert hit the existing _id.
            return false;
        }
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(string holder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        return _locks.DeleteOneAsync(new BsonDocument { { "_id", Name }, { "holder", holder } }, cancellationToken);
    }
}
