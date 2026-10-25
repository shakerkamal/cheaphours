using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence;

/// <summary>Collection names and the indexes the application needs, created idempotently at startup.</summary>
public static class MongoIndexes
{
    /// <summary>Charger registrations.</summary>
    public const string ChargePoints = "chargePoints";

    /// <summary>Devices.</summary>
    public const string Devices = "devices";

    /// <summary>The connection registry.</summary>
    public const string Connections = "connections";

    private const int NamespaceExists = 48;

    /// <summary>Creates every collection and index that does not exist yet; existing ones are left as they are.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the indexes exist.</returns>
    public static async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var keys = Builders<BsonDocument>.IndexKeys;

        // chargePoints is looked up by _id only; the collection is created so its _id index exists up front.
        await EnsureCollectionAsync(database, ChargePoints, cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(Devices).Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(keys.Ascending("providerId").Ascending("externalId"), new CreateIndexOptions { Name = "providerId_externalId", Unique = true }),
                new CreateIndexModel<BsonDocument>(keys.Ascending("ownerId"), new CreateIndexOptions { Name = "ownerId" }),
            ],
            cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(Connections).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys.Ascending("replicaId"), new CreateIndexOptions { Name = "replicaId" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCollectionAsync(IMongoDatabase database, string name, CancellationToken cancellationToken)
    {
        try
        {
            await database.CreateCollectionAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceExists)
        {
            // Already there, perhaps created by another replica starting at the same time.
        }
    }
}
