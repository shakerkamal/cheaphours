using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Plans;
using MongoDB.Driver;

namespace CheapHours.Persistence.Goals;

/// <summary>
/// The <c>goals</c> and <c>goalClaims</c> collections (design §3). Creating and changing a goal write the claims,
/// the goal and the plan version in one transaction, so a crash never leaves a goal without its plan or a device
/// claimed by a half-created goal. A claim conflict or a duplicate plan version aborts the transaction.
/// </summary>
/// <param name="database">The database.</param>
public sealed class GoalStore(IMongoDatabase database) : IGoalStore
{
    private static readonly FilterDefinitionBuilder<GoalDocument> Filter = Builders<GoalDocument>.Filter;
    private static readonly FilterDefinitionBuilder<GoalClaimDocument> ClaimFilter = Builders<GoalClaimDocument>.Filter;

    private readonly IMongoDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMongoCollection<GoalDocument> _goals = Goals(database);
    private readonly IMongoCollection<GoalClaimDocument> _claims = database.GetCollection<GoalClaimDocument>(MongoIndexes.GoalClaims);
    private readonly IMongoCollection<PlanDocument> _plans = PlanStore.Collection(database);

    /// <inheritdoc/>
    public async Task<GoalWriteOutcome> CreateAsync(StoredGoal goal, StoredPlan firstVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(firstVersion);
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    await ClaimAsync(session, goal.Goal.VehicleDeviceId, goal, now, ct).ConfigureAwait(false);
                    await ClaimAsync(session, goal.Goal.ChargerDeviceId, goal, now, ct).ConfigureAwait(false);
                    await _goals.InsertOneAsync(session, ToDocument(goal), cancellationToken: ct).ConfigureAwait(false);
                    return await PlanStore.WriteVersionAsync(_plans, session, firstVersion, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return GoalWriteOutcome.Stored;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return GoalWriteOutcome.DeviceBusy;
        }
    }

    /// <inheritdoc/>
    public async Task<GoalWriteOutcome> ChangeAsync(StoredGoal goal, StoredPlan nextVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(nextVersion);
        var id = goal.Goal.Id.Value;
        var departure = UtcTime.ToBson(goal.Goal.DepartureUtc);
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    await _claims.UpdateManyAsync(
                        session,
                        ClaimFilter.Eq(c => c.GoalId, id),
                        Builders<GoalClaimDocument>.Update.Set(c => c.Until, departure),
                        cancellationToken: ct).ConfigureAwait(false);
                    await _goals.UpdateOneAsync(
                        session,
                        Filter.Eq(d => d.Id, id) & Filter.Eq(d => d.OwnerId, goal.Goal.OwnerId.Value),
                        Builders<GoalDocument>.Update
                            .Set(d => d.TargetSocPercent, goal.Goal.TargetSocPercent)
                            .Set(d => d.DepartureAt, departure)
                            .Set(d => d.UpdatedAt, UtcTime.ToBson(goal.UpdatedAt)),
                        cancellationToken: ct).ConfigureAwait(false);
                    return await PlanStore.WriteVersionAsync(_plans, session, nextVersion, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return GoalWriteOutcome.Stored;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return GoalWriteOutcome.VersionTaken;
        }
    }

    /// <inheritdoc/>
    public async Task<StoredGoal?> GetAsync(OwnerId ownerId, GoalId goalId, CancellationToken cancellationToken)
    {
        var document = await _goals.Find(Filter.Eq(d => d.OwnerId, ownerId.Value) & Filter.Eq(d => d.Id, goalId.Value))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToStored(document);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredGoal>> ListOpenAsync(DateTimeOffset departingAfter, CancellationToken cancellationToken)
    {
        var documents = await _goals.Find(Filter.Gt(d => d.DepartureAt, UtcTime.ToBson(departingAfter)))
            .SortBy(d => d.DepartureAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(ToStored)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredGoal>> ListDepartingAsync(OwnerId ownerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var documents = await _goals.Find(
                Filter.Eq(d => d.OwnerId, ownerId.Value)
                & Filter.Gte(d => d.DepartureAt, UtcTime.ToBson(fromUtc))
                & Filter.Lt(d => d.DepartureAt, UtcTime.ToBson(toUtc)))
            .SortBy(d => d.DepartureAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(ToStored)];
    }

    private static GoalDocument ToDocument(StoredGoal stored) => new()
    {
        Id = stored.Goal.Id.Value,
        OwnerId = stored.Goal.OwnerId.Value,
        VehicleDeviceId = stored.Goal.VehicleDeviceId.Value,
        ChargerDeviceId = stored.Goal.ChargerDeviceId.Value,
        TargetSocPercent = stored.Goal.TargetSocPercent,
        DepartureAt = UtcTime.ToBson(stored.Goal.DepartureUtc),
        PlanId = stored.PlanId.Value,
        CreatedAt = UtcTime.ToBson(stored.CreatedAt),
        UpdatedAt = UtcTime.ToBson(stored.UpdatedAt),
    };

    private static StoredGoal ToStored(GoalDocument d) => new(
        new Goal(GoalId.Parse(d.Id), OwnerId.Parse(d.OwnerId), DeviceId.Parse(d.VehicleDeviceId), DeviceId.Parse(d.ChargerDeviceId), d.TargetSocPercent, UtcTime.FromBson(d.DepartureAt)),
        PlanId.Parse(d.PlanId),
        UtcTime.FromBson(d.CreatedAt),
        UtcTime.FromBson(d.UpdatedAt));

    private static IMongoCollection<GoalDocument> Goals(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<GoalDocument>(MongoIndexes.Goals);
    }

    // Takes the device for this goal until its departure. The filter matches a missing claim (then the upsert
    // inserts), a lapsed claim or this goal's own claim; a live claim of another goal is not matched, so the
    // upsert's insert hits the existing id and fails with a duplicate key.
    private Task<UpdateResult> ClaimAsync(IClientSessionHandle session, DeviceId device, StoredGoal goal, DateTimeOffset now, CancellationToken cancellationToken) =>
        _claims.UpdateOneAsync(
            session,
            ClaimFilter.Eq(c => c.Id, device.Value)
            & (ClaimFilter.Lte(c => c.Until, UtcTime.ToBson(now)) | ClaimFilter.Eq(c => c.GoalId, goal.Goal.Id.Value)),
            Builders<GoalClaimDocument>.Update
                .Set(c => c.GoalId, goal.Goal.Id.Value)
                .Set(c => c.Until, UtcTime.ToBson(goal.Goal.DepartureUtc)),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    private async Task<T> InTransactionAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await session.WithTransactionAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
