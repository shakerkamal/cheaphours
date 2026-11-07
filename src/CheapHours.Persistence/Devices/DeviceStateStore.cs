using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using MongoDB.Driver;

namespace CheapHours.Persistence.Devices;

/// <summary>MongoDB implementation of <see cref="IDeviceStateStore"/> and <see cref="IDeviceCommandStateStore"/>.</summary>
/// <param name="database">The database.</param>
public sealed class DeviceStateStore(IMongoDatabase database) : IDeviceStateStore, IDeviceCommandStateStore
{
    private static readonly FilterDefinitionBuilder<DeviceDocument> Filter = Builders<DeviceDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<DeviceDocument> Update = Builders<DeviceDocument>.Update;

    private readonly IMongoCollection<DeviceDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<DeviceId> EnsureAsync(Device device, TimeSpan reportInterval, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(reportInterval, TimeSpan.FromSeconds(1));
        var intervalSeconds = checked((int)reportInterval.TotalSeconds);
        var when = UtcTime.ToBson(at);
        var filter = Key(device.ProviderId, device.ExternalId);

        var insert = Update
            .SetOnInsert(d => d.Id, device.Id.Value)
            .SetOnInsert(d => d.OwnerId, device.OwnerId.Value)
            .SetOnInsert(d => d.Type, device.Type)
            .SetOnInsert(d => d.Capabilities, DeviceDocumentMapper.Names(device.Capabilities))
            .SetOnInsert(d => d.Attributes, new DeviceDocument.AttributesDocument { MaxPowerW = device.Attributes.MaxPowerW, BatteryCapacityWh = device.Attributes.BatteryCapacityWh })
            .SetOnInsert(d => d.Status, DeviceStatus.Unknown)
            .SetOnInsert(d => d.StatusAt, when)
            .SetOnInsert(d => d.CreatedAt, when)
            .SetOnInsert(d => d.SchemaVersion, 1);

        try
        {
            await _collection.UpdateOneAsync(filter, insert, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent upsert inserted it first; the unique index kept it to one document.
        }

        // Set on every connection, not only on insert: a changed interval applies from the next connection.
        // A device seen again is no longer removed.
        await _collection.UpdateOneAsync(filter, Update.Set(d => d.ReportIntervalSeconds, intervalSeconds).Unset(d => d.RemovedAt), cancellationToken: cancellationToken).ConfigureAwait(false);

        // A charger that connects is no longer offline, even before it reports a status.
        await _collection.UpdateOneAsync(
            filter & Filter.Eq(d => d.Status, DeviceStatus.Offline) & Filter.Lt(d => d.StatusAt, when),
            Update.Set(d => d.Status, DeviceStatus.Unknown).Set(d => d.StatusAt, when),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var id = await _collection.Find(filter).Project(d => d.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
        return DeviceId.Parse(id);
    }

    /// <inheritdoc/>
    public Task RecordHardwareAsync(string providerId, string externalId, DeviceHardware hardware, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        var document = new DeviceDocument.HardwareDocument { Manufacturer = hardware.Manufacturer, Model = hardware.Model, FirmwareVersion = hardware.FirmwareVersion };
        return _collection.UpdateOneAsync(Key(providerId, externalId), Update.Set(d => d.Hardware, document), cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RecordStatusAsync(string providerId, string externalId, DeviceStatus status, DateTimeOffset at, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var when = UtcTime.ToBson(at);
        var received = UtcTime.ToBson(receivedAt);

        // Compare with the last *reported* status only: the Unknown the gateway writes on connect must not
        // hide a report timestamped just before the reconnect. An Offline at or after the report still wins.
        var newerThanLastReport = Filter.Or(Filter.Eq(d => d.ReportedStatusAt, null), Filter.Lt(d => d.ReportedStatusAt, when));
        var notOfflineSince = Filter.Not(Filter.Eq(d => d.Status, DeviceStatus.Offline) & Filter.Gte(d => d.StatusAt, when));

        var result = await _collection.UpdateOneAsync(
            Key(providerId, externalId) & newerThanLastReport & notOfflineSince,
            Update.Set(d => d.Status, status).Set(d => d.StatusAt, when).Set(d => d.ReportedStatusAt, when).Set(d => d.StatusReceivedAt, received),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ModifiedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkOfflineAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var when = UtcTime.ToBson(at);
        var result = await _collection.UpdateOneAsync(
            Key(providerId, externalId) & Filter.Lt(d => d.StatusAt, when),
            Update.Set(d => d.Status, DeviceStatus.Offline).Set(d => d.StatusAt, when),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ModifiedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkRemovedAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var when = UtcTime.ToBson(at);
        await _collection.UpdateOneAsync(Key(providerId, externalId), Update.Set(d => d.RemovedAt, when), cancellationToken: cancellationToken).ConfigureAwait(false);
        return await MarkOfflineAsync(providerId, externalId, at, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task RecordSeenAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken) =>
        _collection.UpdateOneAsync(Key(providerId, externalId), Update.Max(d => d.LastSeenAt, UtcTime.ToBson(at)), cancellationToken: cancellationToken);

    /// <inheritdoc/>
    public async Task<Device?> FindAsync(string providerId, string externalId, CancellationToken cancellationToken)
    {
        var d = await _collection.Find(Key(providerId, externalId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return d is null ? null : DeviceDocumentMapper.ToDevice(d);
    }

    /// <inheritdoc/>
    public Task<StoredDevice?> GetAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        FirstStoredAsync(Filter.Eq(d => d.Id, deviceId.Value), cancellationToken);

    /// <inheritdoc/>
    public Task<StoredDevice?> GetAsync(OwnerId ownerId, DeviceId deviceId, CancellationToken cancellationToken) =>
        FirstStoredAsync(Filter.Eq(d => d.OwnerId, ownerId.Value) & Filter.Eq(d => d.Id, deviceId.Value), cancellationToken);

    /// <inheritdoc/>
    Task<StoredDevice?> IDeviceCommandStateStore.FindAsync(string providerId, string externalId, CancellationToken cancellationToken) =>
        FirstStoredAsync(Key(providerId, externalId), cancellationToken);

    /// <inheritdoc/>
    public Task SetActiveSessionAsync(string providerId, string externalId, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _collection.UpdateOneAsync(Key(providerId, externalId), Update.Set(d => d.ActiveSessionId, sessionId), cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> ClearActiveSessionAsync(string providerId, string externalId, string? sessionId, CancellationToken cancellationToken)
    {
        var running = sessionId is null ? Filter.Ne(d => d.ActiveSessionId, null) : Filter.Eq(d => d.ActiveSessionId, sessionId);
        var result = await _collection.UpdateOneAsync(
            Key(providerId, externalId) & running,
            Update.Unset(d => d.ActiveSessionId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount > 0;
    }

    private async Task<StoredDevice?> FirstStoredAsync(FilterDefinition<DeviceDocument> filter, CancellationToken cancellationToken)
    {
        var d = await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return d is null
            ? null
            : new StoredDevice(DeviceDocumentMapper.ToDevice(d), UtcTime.FromBson(d.StatusReceivedAt), d.ActiveSessionId, UtcTime.FromBson(d.RemovedAt));
    }

    private static FilterDefinition<DeviceDocument> Key(string providerId, string externalId) =>
        Filter.Eq(d => d.ProviderId, providerId) & Filter.Eq(d => d.ExternalId, externalId);

    private static IMongoCollection<DeviceDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<DeviceDocument>(MongoIndexes.Devices);
    }
}
