using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Persistence.Plans;

/// <summary>
/// The <c>plans</c> collection (design §3). A re-plan inserts the next version and supersedes the previous one in
/// one transaction; the version's id makes a concurrent duplicate fail, which aborts the transaction.
/// </summary>
/// <param name="database">The database.</param>
public sealed class PlanStore(IMongoDatabase database) : IPlanStore
{
    private static readonly FilterDefinitionBuilder<PlanDocument> Filter = Builders<PlanDocument>.Filter;

    private readonly IMongoDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMongoCollection<PlanDocument> _plans = Collection(database);

    /// <inheritdoc/>
    public async Task<StoredPlan?> GetCurrentAsync(GoalId goalId, CancellationToken cancellationToken)
    {
        var document = await _plans.Find(Filter.Eq(d => d.GoalId, goalId.Value))
            .SortByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : PlanDocumentMapper.ToStored(document);
    }

    /// <inheritdoc/>
    public async Task<StoredPlan?> GetCurrentAsync(OwnerId ownerId, PlanId planId, CancellationToken cancellationToken)
    {
        var document = await _plans.Find(Filter.Eq(d => d.PlanId, planId.Value) & Filter.Eq(d => d.OwnerId, ownerId.Value))
            .SortByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : PlanDocumentMapper.ToStored(document);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredPlan>> GetCurrentManyAsync(OwnerId ownerId, IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        if (goalIds.Count == 0)
        {
            return [];
        }

        // Highest version first within each goal, then the first document of each group (served by ownerId_goalId).
        var documents = await _plans.Aggregate()
            .Match(Filter.Eq(d => d.OwnerId, ownerId.Value) & Filter.In(d => d.GoalId, goalIds.Select(g => g.Value)))
            .SortBy(d => d.GoalId).ThenByDescending(d => d.Version)
            .Group(new BsonDocument { { "_id", "$goalId" }, { "current", new BsonDocument("$first", "$$ROOT") } })
            .ReplaceRoot(new BsonValueAggregateExpressionDefinition<BsonDocument, PlanDocument>("$current"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(PlanDocumentMapper.ToStored)];
    }

    /// <inheritdoc/>
    public async Task<StoredPlan?> GetRunningAsync(GoalId goalId, CancellationToken cancellationToken)
    {
        var document = await _plans.Find(Filter.Eq(d => d.GoalId, goalId.Value) & Filter.Eq(d => d.Status, PlanStatus.Confirmed))
            .SortByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : PlanDocumentMapper.ToStored(document);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredPlan>> ListVersionsAsync(GoalId goalId, CancellationToken cancellationToken)
    {
        var documents = await _plans.Find(Filter.Eq(d => d.GoalId, goalId.Value)).SortBy(d => d.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(PlanDocumentMapper.ToStored)];
    }

    /// <inheritdoc/>
    public async Task<bool> AddVersionAsync(StoredPlan version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        try
        {
            using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.WithTransactionAsync(
                (s, ct) => WriteVersionAsync(_plans, s, version, ct),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredPlan>> LatestConfirmedAsync(OwnerId ownerId, IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        if (goalIds.Count == 0)
        {
            return [];
        }

        var documents = await _plans.Find(
                Filter.Eq(d => d.OwnerId, ownerId.Value)
                & Filter.In(d => d.GoalId, goalIds.Select(g => g.Value))
                & Filter.Ne(d => d.ConfirmedAt, null))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.GroupBy(d => d.GoalId).Select(g => PlanDocumentMapper.ToStored(g.MaxBy(d => d.Version)!))];
    }

    /// <summary>
    /// Inserts <paramref name="next"/> and supersedes its predecessor inside <paramref name="session"/>'s transaction.
    /// Shared with the goal store, which writes a goal change and its new version together.
    /// </summary>
    internal static async Task<bool> WriteVersionAsync(IMongoCollection<PlanDocument> plans, IClientSessionHandle session, StoredPlan next, CancellationToken cancellationToken)
    {
        await plans.InsertOneAsync(session, PlanDocumentMapper.ToDocument(next), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (next.Plan.Version > 1)
        {
            var previous = StoredPlan.VersionIdOf(next.Plan.Id.Value, next.Plan.Version - 1);
            await plans.UpdateOneAsync(
                session,
                Filter.Eq(d => d.Id, previous),
                Builders<PlanDocument>.Update.Set(d => d.Status, PlanStatus.Superseded),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    internal static IMongoCollection<PlanDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<PlanDocument>(MongoIndexes.Plans);
    }
}
