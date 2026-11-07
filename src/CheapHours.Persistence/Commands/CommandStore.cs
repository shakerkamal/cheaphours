using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Outbox;
using MongoDB.Driver;

namespace CheapHours.Persistence.Commands;

/// <summary>
/// The <c>commands</c> collection (command-dispatch design §2). Every write filters on the version it read and
/// increments it, so concurrent writers never overwrite each other: the loser writes nothing and decides again
/// from a fresh read. Writes that announce a command add its outbox entry in the same transaction.
/// </summary>
/// <param name="database">The database.</param>
public sealed class CommandStore(IMongoDatabase database) : ICommandStore
{
    private static readonly FilterDefinitionBuilder<CommandDocument> Filter = Builders<CommandDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<CommandDocument> Update = Builders<CommandDocument>.Update;
    private static readonly CommandState[] OpenStates = [.. Enum.GetValues<CommandState>().Where(s => !s.IsTerminal())];

    private readonly IMongoDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly IMongoCollection<CommandDocument> _commands = Collection(database);
    private readonly IMongoCollection<OutboxDocument> _outbox = OutboxStore.Collection(database);

    /// <inheritdoc/>
    public async Task<bool> CreateWithOutboxAsync(StoredCommand command, OutboxEntry announcement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(announcement);
        try
        {
            await InTransactionAsync(
                async (session, ct) =>
                {
                    await InsertAsync(_commands, session, [command], ct).ConfigureAwait(false);
                    await OutboxStore.AddAsync(_outbox, session, [announcement], ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
        catch (MongoBulkWriteException ex) when (ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public Task<StoredCommand?> GetAsync(OwnerId ownerId, CommandId commandId, CancellationToken cancellationToken) =>
        FirstAsync(Filter.Eq(d => d.OwnerId, ownerId.Value) & Filter.Eq(d => d.Id, commandId.Value), cancellationToken);

    /// <inheritdoc/>
    public Task<StoredCommand?> FindAsync(CommandId commandId, CancellationToken cancellationToken) =>
        FirstAsync(Filter.Eq(d => d.Id, commandId.Value), cancellationToken);

    /// <inheritdoc/>
    public async Task<StoredCommand?> SaveAsync(StoredCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var saved = await ReplaceAsync(_commands, null, command, cancellationToken).ConfigureAwait(false);
        return saved ? command with { Version = command.Version + 1 } : null;
    }

    /// <inheritdoc/>
    public async Task<StoredCommand?> SaveWithOutboxAsync(StoredCommand command, OutboxEntry announcement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(announcement);
        var saved = await InTransactionAsync(
            async (session, ct) =>
            {
                if (!await ReplaceAsync(_commands, session, command, ct).ConfigureAwait(false))
                {
                    // Nothing else has been written in this transaction yet, so committing it is a no-op.
                    return false;
                }

                await OutboxStore.AddAsync(_outbox, session, [announcement], ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return saved ? command with { Version = command.Version + 1 } : null;
    }

    /// <inheritdoc/>
    public async Task<StoredCommand?> TryClaimAsync(CommandId commandId, string claimant, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var at = UtcTime.ToBson(now);
        var document = await _commands.FindOneAndUpdateAsync(
            Filter.Eq(d => d.Id, commandId.Value) & Sendable(at),
            Update
                .Set(d => d.Claim, new CommandDocument.ClaimDocument { By = claimant, Until = UtcTime.ToBson(now + duration) })
                .Inc(d => d.Version, 1),
            new FindOneAndUpdateOptions<CommandDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken).ConfigureAwait(false);
        return document is null ? null : CommandDocumentMapper.ToStored(document);
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseClaimAsync(CommandId commandId, string claimant, CancellationToken cancellationToken)
    {
        var result = await _commands.UpdateOneAsync(
            Filter.Eq(d => d.Id, commandId.Value) & Filter.Eq(d => d.Claim!.By, claimant),
            Update.Unset(d => d.Claim).Inc(d => d.Version, 1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<long> ExpireClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await _commands.UpdateManyAsync(
            Filter.Ne(d => d.Claim, null) & Filter.Lte(d => d.Claim!.Until, UtcTime.ToBson(now)),
            Update.Unset(d => d.Claim).Inc(d => d.Version, 1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredCommand>> ListInStateAsync(CommandState state, int limit, CancellationToken cancellationToken) =>
        ListAsync(Filter.Eq(d => d.State, state), limit, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredCommand>> ListSendableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        ListAsync(Sendable(UtcTime.ToBson(now)), limit, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredCommand>> ListOpenForDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        ListAsync(Filter.Eq(d => d.DeviceId, deviceId.Value) & Filter.In(d => d.State, OpenStates), limit: 0, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredCommand>> ListForPlanAsync(PlanId planId, CancellationToken cancellationToken) =>
        ListAsync(Filter.Eq(d => d.PlanId, planId.Value), limit: 0, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredCommand>> ListForPlanVersionAsync(PlanId planId, int version, CancellationToken cancellationToken) =>
        ListAsync(Filter.Eq(d => d.PlanId, planId.Value) & Filter.Eq(d => d.PlanVersion, version), limit: 0, cancellationToken);

    /// <summary>Inserts new commands inside <paramref name="session"/>'s transaction.</summary>
    internal static Task InsertAsync(IMongoCollection<CommandDocument> commands, IClientSessionHandle session, IReadOnlyCollection<StoredCommand> created, CancellationToken cancellationToken) =>
        created.Count == 0
            ? Task.CompletedTask
            : commands.InsertManyAsync(session, created.Select(c => CommandDocumentMapper.ToDocument(c, c.Version)), cancellationToken: cancellationToken);

    /// <summary>
    /// Cancels a plan version's unsent commands inside <paramref name="session"/>'s transaction and deletes their
    /// unpublished outbox entries. A claimed command is being handed to its device and is left to finish.
    /// </summary>
    /// <returns>The ids of the cancelled commands.</returns>
    internal static async Task<IReadOnlyList<CommandId>> CancelUnsentAsync(
        IMongoCollection<CommandDocument> commands,
        IMongoCollection<OutboxDocument> outbox,
        IClientSessionHandle session,
        PlanId planId,
        int version,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var documents = await commands.Find(
                session,
                Filter.Eq(d => d.PlanId, planId.Value)
                & Filter.Eq(d => d.PlanVersion, version)
                & Filter.In(d => d.State, [CommandState.Pending, CommandState.Published]))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var cancelled = new List<CommandId>();
        foreach (var stored in documents.Select(CommandDocumentMapper.ToStored).Where(c => c.IsUnsentAt(now)))
        {
            var last = stored.Command.History[^1].At;
            stored.Command.Cancel(now > last ? now : last, reason);
            var cleared = stored with { Claim = null };

            // A concurrent claim after this transaction's snapshot surfaces as a write conflict, which makes the
            // driver retry the whole transaction; a mismatch here means the snapshot itself was stale.
            if (!await ReplaceAsync(commands, session, cleared, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Command {stored.Id} changed while it was being cancelled.");
            }

            cancelled.Add(stored.Id);
        }

        await OutboxStore.DeleteUnpublishedAsync(outbox, session, cancelled, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    internal static IMongoCollection<CommandDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<CommandDocument>(MongoIndexes.Commands);
    }

    // Published, due, past its retry time and free of a live claim.
    private static FilterDefinition<CommandDocument> Sendable(DateTime at) =>
        Filter.Eq(d => d.State, CommandState.Published)
        & Filter.Lte(d => d.DueAt, at)
        & (Filter.Eq(d => d.NextAttemptAt, null) | Filter.Lte(d => d.NextAttemptAt, at))
        & (Filter.Eq(d => d.Claim, null) | Filter.Lte(d => d.Claim!.Until, at));

    private static async Task<bool> ReplaceAsync(IMongoCollection<CommandDocument> commands, IClientSessionHandle? session, StoredCommand command, CancellationToken cancellationToken)
    {
        var filter = Filter.Eq(d => d.Id, command.Id.Value) & Filter.Eq(d => d.Version, command.Version);
        var document = CommandDocumentMapper.ToDocument(command, command.Version + 1);
        var result = session is null
            ? await commands.ReplaceOneAsync(filter, document, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await commands.ReplaceOneAsync(session, filter, document, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount > 0;
    }

    private async Task<StoredCommand?> FirstAsync(FilterDefinition<CommandDocument> filter, CancellationToken cancellationToken)
    {
        var document = await _commands.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : CommandDocumentMapper.ToStored(document);
    }

    private async Task<IReadOnlyList<StoredCommand>> ListAsync(FilterDefinition<CommandDocument> filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var find = _commands.Find(filter).SortBy(d => d.DueAt).ThenBy(d => d.Id);
        var documents = await (limit > 0 ? find.Limit(limit) : find).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(CommandDocumentMapper.ToStored)];
    }

    private async Task<T> InTransactionAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await session.WithTransactionAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
