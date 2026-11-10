using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Outbox;
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
    private static readonly FilterDefinitionBuilder<PlanDocument> PlanFilter = Builders<PlanDocument>.Filter;

    private readonly IMongoDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMongoCollection<GoalDocument> _goals = Goals(database);
    private readonly IMongoCollection<GoalClaimDocument> _claims = database.GetCollection<GoalClaimDocument>(MongoIndexes.GoalClaims);
    private readonly IMongoCollection<PlanDocument> _plans = PlanStore.Collection(database);
    private readonly IMongoCollection<CommandDocument> _commands = CommandStore.Collection(database);
    private readonly IMongoCollection<OutboxDocument> _outbox = OutboxStore.Collection(database);

    /// <inheritdoc/>
    public async Task<GoalWriteOutcome> AddChangeDraftAsync(StoredPlan draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    await _plans.InsertOneAsync(session, PlanDocumentMapper.ToDocument(draft), cancellationToken: ct).ConfigureAwait(false);
                    await _plans.UpdateManyAsync(
                        session,
                        PlanFilter.Eq(d => d.PlanId, draft.Plan.Id.Value)
                        & PlanFilter.Lt(d => d.Version, draft.Plan.Version)
                        & PlanFilter.Eq(d => d.Status, PlanStatus.Draft),
                        Builders<PlanDocument>.Update.Set(d => d.Status, PlanStatus.Superseded),
                        cancellationToken: ct).ConfigureAwait(false);
                    return true;
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
    public async Task<GoalWriteOutcome> ConfirmAsync(StoredPlan draft, StoredPlan? running, StoredGoal? changedGoal, CommandBatch commands, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(commands);
        var plan = draft.Plan;
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    // Only the current draft can be confirmed: no version above it, and still a draft.
                    var confirmed = await _plans.UpdateOneAsync(
                        session,
                        PlanFilter.Eq(d => d.Id, draft.VersionId) & PlanFilter.Eq(d => d.Status, PlanStatus.Draft),
                        Builders<PlanDocument>.Update.Set(d => d.Status, PlanStatus.Confirmed).Set(d => d.ConfirmedAt, UtcTime.ToBson(now)),
                        cancellationToken: ct).ConfigureAwait(false);
                    if (confirmed.ModifiedCount == 0 || await HasVersionAboveAsync(session, plan.Id, plan.Version, status: null, ct).ConfigureAwait(false))
                    {
                        throw new PlanChangedException();
                    }

                    if (running is not null)
                    {
                        await SupersedeRunningAsync(session, running, ct).ConfigureAwait(false);
                        await CommandStore.CancelUnsentAsync(_commands, _outbox, session, plan.Id, running.Plan.Version, SupersededBy(draft), now, ct).ConfigureAwait(false);
                    }

                    if (changedGoal is not null)
                    {
                        await ApplyGoalValuesAsync(session, changedGoal, ct).ConfigureAwait(false);
                    }

                    await CommandStore.InsertAsync(_commands, session, commands.Commands, ct).ConfigureAwait(false);
                    await OutboxStore.AddAsync(_outbox, session, commands.Announcements, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return GoalWriteOutcome.Stored;
        }
        catch (PlanChangedException)
        {
            return GoalWriteOutcome.PlanChanged;
        }
    }

    /// <inheritdoc/>
    public async Task<GoalWriteOutcome> ReplanConfirmedAsync(StoredPlan nextVersion, StoredPlan running, CommandBatch commands, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextVersion);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(commands);
        var plan = nextVersion.Plan;
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    await _plans.InsertOneAsync(session, PlanDocumentMapper.ToDocument(nextVersion), cancellationToken: ct).ConfigureAwait(false);
                    await SupersedeRunningAsync(session, running, ct).ConfigureAwait(false);

                    // A change draft above the running version is replaced by this re-plan.
                    await _plans.UpdateManyAsync(
                        session,
                        PlanFilter.Eq(d => d.PlanId, plan.Id.Value)
                        & PlanFilter.Gt(d => d.Version, running.Plan.Version)
                        & PlanFilter.Lt(d => d.Version, plan.Version)
                        & PlanFilter.Eq(d => d.Status, PlanStatus.Draft),
                        Builders<PlanDocument>.Update.Set(d => d.Status, PlanStatus.Superseded),
                        cancellationToken: ct).ConfigureAwait(false);

                    // A version confirmed in between (a change draft confirmed meanwhile) means this re-plan is stale.
                    if (await HasVersionAboveAsync(session, plan.Id, running.Plan.Version, PlanStatus.Confirmed, ct, below: plan.Version).ConfigureAwait(false))
                    {
                        throw new PlanChangedException();
                    }

                    await CommandStore.CancelUnsentAsync(_commands, _outbox, session, plan.Id, running.Plan.Version, SupersededBy(nextVersion), now, ct).ConfigureAwait(false);
                    await CommandStore.InsertAsync(_commands, session, commands.Commands, ct).ConfigureAwait(false);
                    await OutboxStore.AddAsync(_outbox, session, commands.Announcements, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return GoalWriteOutcome.Stored;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return GoalWriteOutcome.VersionTaken;
        }
        catch (PlanChangedException)
        {
            return GoalWriteOutcome.PlanChanged;
        }
    }

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
    public async Task<IReadOnlyList<StoredGoal>> ListAsync(OwnerId ownerId, GoalId? before, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var filter = Filter.Eq(d => d.OwnerId, ownerId.Value);
        if (before is { } below)
        {
            filter &= Filter.Lt(d => d.Id, below.Value);
        }

        var documents = await _goals.Find(filter)
            .SortByDescending(d => d.Id)
            .Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(ToStored)];
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

    private static string SupersededBy(StoredPlan version) => $"Superseded by {version.VersionId}";

    // The running version must still be confirmed when it is superseded; anything else means another writer moved
    // the plan on, and this transaction must not write.
    private async Task SupersedeRunningAsync(IClientSessionHandle session, StoredPlan running, CancellationToken cancellationToken)
    {
        var superseded = await _plans.UpdateOneAsync(
            session,
            PlanFilter.Eq(d => d.Id, running.VersionId) & PlanFilter.Eq(d => d.Status, PlanStatus.Confirmed),
            Builders<PlanDocument>.Update.Set(d => d.Status, PlanStatus.Superseded),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (superseded.ModifiedCount == 0)
        {
            throw new PlanChangedException();
        }
    }

    private Task<bool> HasVersionAboveAsync(IClientSessionHandle session, PlanId planId, int version, PlanStatus? status, CancellationToken cancellationToken, int? below = null)
    {
        var filter = PlanFilter.Eq(d => d.PlanId, planId.Value) & PlanFilter.Gt(d => d.Version, version);
        if (status is { } s)
        {
            filter &= PlanFilter.Eq(d => d.Status, s);
        }

        if (below is { } upper)
        {
            filter &= PlanFilter.Lt(d => d.Version, upper);
        }

        return _plans.Find(session, filter).AnyAsync(cancellationToken);
    }

    private async Task ApplyGoalValuesAsync(IClientSessionHandle session, StoredGoal goal, CancellationToken cancellationToken)
    {
        var id = goal.Goal.Id.Value;
        var departure = UtcTime.ToBson(goal.Goal.DepartureUtc);
        await _claims.UpdateManyAsync(
            session,
            ClaimFilter.Eq(c => c.GoalId, id),
            Builders<GoalClaimDocument>.Update.Set(c => c.Until, departure),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _goals.UpdateOneAsync(
            session,
            Filter.Eq(d => d.Id, id) & Filter.Eq(d => d.OwnerId, goal.Goal.OwnerId.Value),
            Builders<GoalDocument>.Update
                .Set(d => d.TargetSocPercent, goal.Goal.TargetSocPercent)
                .Set(d => d.DepartureAt, departure)
                .Set(d => d.UpdatedAt, UtcTime.ToBson(goal.UpdatedAt)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> InTransactionAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await session.WithTransactionAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Thrown inside a transaction body to abort it when the plan moved on; never leaves the store.
    private sealed class PlanChangedException : Exception
    {
    }
}
