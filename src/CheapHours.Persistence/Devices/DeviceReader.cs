using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using MongoDB.Driver;

namespace CheapHours.Persistence.Devices;

/// <summary>MongoDB implementation of <see cref="IDeviceReader"/>, served by the <c>ownerId_id</c> index.</summary>
/// <param name="database">The database.</param>
public sealed class DeviceReader(IMongoDatabase database) : IDeviceReader
{
    private static readonly FilterDefinitionBuilder<DeviceDocument> Filter = Builders<DeviceDocument>.Filter;

    private readonly IMongoCollection<DeviceDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<Device?> GetAsync(OwnerId ownerId, DeviceId deviceId, CancellationToken cancellationToken)
    {
        var d = await _collection
            .Find(Owner(ownerId) & Filter.Eq(x => x.Id, deviceId.Value))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return d is null ? null : DeviceDocumentMapper.ToDevice(d);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Device>> ListAsync(OwnerId ownerId, DeviceId? after, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var filter = after is { } a ? Owner(ownerId) & Filter.Gt(x => x.Id, a.Value) : Owner(ownerId);
        var documents = await _collection
            .Find(filter)
            .Sort(Builders<DeviceDocument>.Sort.Ascending(x => x.Id))
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents.ConvertAll(DeviceDocumentMapper.ToDevice);
    }

    private static FilterDefinition<DeviceDocument> Owner(OwnerId ownerId) => Filter.Eq(x => x.OwnerId, ownerId.Value);

    private static IMongoCollection<DeviceDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<DeviceDocument>(MongoIndexes.Devices);
    }
}
