using MongoDB.Driver;

namespace CheapHours.Persistence.Idempotency;

/// <summary>
/// The <c>idempotencyKeys</c> collection (design §10). A first request inserts an in-progress record; a repeat
/// hits the existing id and reads it. Replacing an expired or abandoned record is conditional on the record
/// read, so two repeats racing for it let exactly one through.
/// </summary>
/// <param name="database">The database.</param>
public sealed class IdempotencyStore(IMongoDatabase database) : IIdempotencyStore
{
    private static readonly FilterDefinitionBuilder<IdempotencyDocument> Filter = Builders<IdempotencyDocument>.Filter;

    private readonly IMongoCollection<IdempotencyDocument> _keys = Collection(database);

    /// <inheritdoc/>
    public async Task<IdempotencyClaim> StartAsync(string id, string fingerprint, DateTimeOffset now, TimeSpan keepFor, TimeSpan abandonAfter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var fresh = new IdempotencyDocument
        {
            Id = id,
            Fingerprint = fingerprint,
            State = IdempotencyState.InProgress,
            StartedAt = UtcTime.ToBson(now),
            ExpiresAt = UtcTime.ToBson(now + keepFor),
        };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _keys.InsertOneAsync(fresh, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new IdempotencyClaim.Started();
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // A record exists; decide from it below.
            }

            var existing = await _keys.Find(Filter.Eq(d => d.Id, id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                continue; // deleted in between (forgotten or expired): insert again
            }

            var expired = UtcTime.FromBson(existing.ExpiresAt) <= now;
            var abandoned = existing.State == IdempotencyState.InProgress && UtcTime.FromBson(existing.StartedAt) + abandonAfter <= now;
            if (expired || abandoned)
            {
                // Take it over only if it is still the record just read.
                var replaced = await _keys.ReplaceOneAsync(
                    Filter.Eq(d => d.Id, id) & Filter.Eq(d => d.StartedAt, existing.StartedAt) & Filter.Eq(d => d.State, existing.State),
                    fresh,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (replaced.ModifiedCount == 1)
                {
                    return new IdempotencyClaim.Started();
                }

                continue;
            }

            if (existing.State == IdempotencyState.InProgress)
            {
                return new IdempotencyClaim.InFlight();
            }

            return existing.Fingerprint == fingerprint && existing.Response is { } response
                ? new IdempotencyClaim.Replay(new StoredResponse(response.Status, response.ContentType, response.Location, response.Body))
                : new IdempotencyClaim.Reused();
        }

        return new IdempotencyClaim.InFlight();
    }

    /// <inheritdoc/>
    public Task CompleteAsync(string id, StoredResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        return _keys.UpdateOneAsync(
            Filter.Eq(d => d.Id, id) & Filter.Eq(d => d.State, IdempotencyState.InProgress),
            Builders<IdempotencyDocument>.Update
                .Set(d => d.State, IdempotencyState.Completed)
                .Set(d => d.Response, new IdempotencyDocument.ResponseDocument
                {
                    Status = response.Status,
                    ContentType = response.ContentType,
                    Location = response.Location,
                    Body = response.Body,
                }),
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public Task ForgetAsync(string id, CancellationToken cancellationToken) =>
        _keys.DeleteOneAsync(Filter.Eq(d => d.Id, id) & Filter.Eq(d => d.State, IdempotencyState.InProgress), cancellationToken);

    private static IMongoCollection<IdempotencyDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<IdempotencyDocument>(MongoIndexes.IdempotencyKeys);
    }
}
