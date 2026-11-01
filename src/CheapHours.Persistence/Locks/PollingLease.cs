using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Locks;

/// <summary>
/// The polling lease on <c>locks</c> (design §5): one document <c>{ _id: "device-polling", holder, expiresAt }</c>.
/// Acquire and renew are one upsert filtered on the lease being expired or already ours; a duplicate-key
/// error means another replica holds it. Release deletes it only when it is ours.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="time">The clock.</param>
public sealed class PollingLease(IMongoDatabase database, TimeProvider time) : IPollingLease
{
    /// <summary>The lease's id.</summary>
    public const string Name = "device-polling";

    private readonly IMongoCollection<BsonDocument> _locks = (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(MongoIndexes.Locks);

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
