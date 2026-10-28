using MongoDB.Driver;

namespace CheapHours.Persistence.ApiKeys;

/// <summary>MongoDB implementation of <see cref="IStoredApiKeys"/>; lookups are by <c>_id</c>.</summary>
/// <param name="database">The database.</param>
public sealed class StoredApiKeys(IMongoDatabase database) : IStoredApiKeys
{
    private static readonly UpdateDefinitionBuilder<ApiKeyDocument> Update = Builders<ApiKeyDocument>.Update;

    private readonly IMongoCollection<ApiKeyDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<StoredApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken)
    {
        var d = await _collection.Find(x => x.Id == keyHash).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return d is null ? null : new StoredApiKey(d.Id, d.Name, d.OwnerId, d.Scopes);
    }

    /// <inheritdoc/>
    public Task SeedAsync(StoredApiKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var when = UtcTime.ToBson(now);

        var update = Update
            .Set(x => x.Name, key.Name)
            .Set(x => x.OwnerId, key.OwnerId)
            .Set(x => x.Scopes, key.Scopes.ToList())
            .Set(x => x.UpdatedAt, when)
            .Set(x => x.SchemaVersion, 1)
            .SetOnInsert(x => x.CreatedAt, when);

        return _collection.UpdateOneAsync(x => x.Id == key.KeyHash, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    private static IMongoCollection<ApiKeyDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<ApiKeyDocument>(MongoIndexes.ApiKeys);
    }
}
