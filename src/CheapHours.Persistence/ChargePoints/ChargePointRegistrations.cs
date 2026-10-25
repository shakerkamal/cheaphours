using CheapHours.Core.Ids;
using MongoDB.Driver;

namespace CheapHours.Persistence.ChargePoints;

/// <summary>MongoDB implementation of <see cref="IChargePointRegistrations"/>.</summary>
/// <param name="database">The database.</param>
public sealed class ChargePointRegistrations(IMongoDatabase database) : IChargePointRegistrations
{
    private static readonly UpdateDefinitionBuilder<ChargePointDocument> Update = Builders<ChargePointDocument>.Update;

    private readonly IMongoCollection<ChargePointDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<ChargePointRegistration?> FindAsync(string chargePointId, CancellationToken cancellationToken)
    {
        var document = await _collection.Find(d => d.Id == chargePointId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return document is null
            ? null
            : new ChargePointRegistration(
                document.Id,
                OwnerId.Parse(document.OwnerId),
                document.PasswordHash,
                document.MaxPowerW,
                UtcTime.FromBson(document.CreatedAt),
                UtcTime.FromBson(document.RevokedAt));
    }

    /// <inheritdoc/>
    public Task SeedAsync(ChargePointRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var update = Update
            .Set(d => d.OwnerId, registration.OwnerId.Value)
            .Set(d => d.PasswordHash, registration.PasswordHash)
            .Set(d => d.MaxPowerW, registration.MaxPowerW)
            .Set(d => d.SchemaVersion, 1)
            .SetOnInsert(d => d.CreatedAt, UtcTime.ToBson(now));

        return _collection.UpdateOneAsync(d => d.Id == registration.ChargePointId, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RevokeAsync(string chargePointId, DateTimeOffset at, CancellationToken cancellationToken) =>
        _collection.UpdateOneAsync(d => d.Id == chargePointId, Update.Set(d => d.RevokedAt, UtcTime.ToBson(at)), cancellationToken: cancellationToken);

    private static IMongoCollection<ChargePointDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<ChargePointDocument>(MongoIndexes.ChargePoints);
    }
}
