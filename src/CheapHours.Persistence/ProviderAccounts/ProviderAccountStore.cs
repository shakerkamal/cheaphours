using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using MongoDB.Driver;

namespace CheapHours.Persistence.ProviderAccounts;

/// <summary>
/// MongoDB store for linked accounts (<c>providerAccounts</c>): the kit's encrypted tokens with a version for
/// compare-and-set writes, the reconnect-needed mark, and the poll schedule.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="time">The clock, for the creation and update times.</param>
public sealed class ProviderAccountStore(IMongoDatabase database, TimeProvider time) : IProviderAccountRepository, IPollingSchedule
{
    private static readonly FilterDefinitionBuilder<ProviderAccountDocument> Filter = Builders<ProviderAccountDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<ProviderAccountDocument> Update = Builders<ProviderAccountDocument>.Update;

    private readonly IMongoCollection<ProviderAccountDocument> _collection = Collection(database);

    /// <inheritdoc/>
    public async Task<SeedOutcome> SeedAsync(ProviderAccount account, string protectedTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(protectedTokens);
        var now = UtcTime.ToBson(time.GetUtcNow());

        var insert = Update
            .SetOnInsert(a => a.Id, Id(account))
            .SetOnInsert(a => a.OwnerId, account.OwnerId.Value)
            .SetOnInsert(a => a.Tokens, protectedTokens)
            .SetOnInsert(a => a.TokenVersion, 1L)
            .SetOnInsert(a => a.ReconnectNeeded, false)
            .SetOnInsert(a => a.CreatedAt, now)
            .SetOnInsert(a => a.UpdatedAt, now)
            .SetOnInsert(a => a.SchemaVersion, 1);
        try
        {
            var inserted = await _collection.UpdateOneAsync(Key(account), insert, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
            if (inserted.UpsertedId is not null)
            {
                return SeedOutcome.Inserted;
            }
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent seed inserted it first; any other duplicate (the owner already linked another
            // account of this provider) is a configuration error and propagates.
            if (!await ExistsAsync(account, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }

        // Re-link only an account marked reconnect-needed; it is polled at once.
        var relinked = await _collection.UpdateOneAsync(
            Key(account) & Filter.Eq(a => a.ReconnectNeeded, true),
            Update.Set(a => a.Tokens, protectedTokens)
                .Inc(a => a.TokenVersion, 1L)
                .Set(a => a.ReconnectNeeded, false)
                .Unset(a => a.NextPollAt)
                .Unset(a => a.PausedUntil)
                .Set(a => a.UpdatedAt, now),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return relinked.ModifiedCount > 0 ? SeedOutcome.Relinked : SeedOutcome.Unchanged;
    }

    /// <inheritdoc/>
    public async Task<StoredAccountTokens?> ReadTokensAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var document = await _collection.Find(Key(account)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : new StoredAccountTokens(document.Tokens, document.TokenVersion, document.ReconnectNeeded);
    }

    /// <inheritdoc/>
    public async Task<bool> TryWriteTokensAsync(ProviderAccount account, string protectedTokens, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(protectedTokens);
        var result = await _collection.UpdateOneAsync(
            Key(account) & Filter.Eq(a => a.TokenVersion, expectedVersion),
            Update.Set(a => a.Tokens, protectedTokens).Inc(a => a.TokenVersion, 1L).Set(a => a.UpdatedAt, UtcTime.ToBson(time.GetUtcNow())),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    /// <inheritdoc/>
    public Task MarkReconnectNeededAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        return _collection.UpdateOneAsync(
            Key(account),
            Update.Set(a => a.ReconnectNeeded, true).Set(a => a.UpdatedAt, UtcTime.ToBson(time.GetUtcNow())),
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderAccount>> ListDueAsync(string providerId, DateTimeOffset now, IReadOnlyCollection<string> exclude, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var when = UtcTime.ToBson(now);

        var filter = Filter.Eq(a => a.ProviderId, providerId)
            & Filter.Eq(a => a.ReconnectNeeded, false)
            & Filter.Or(Filter.Eq(a => a.NextPollAt, null), Filter.Lte(a => a.NextPollAt, when))
            & Filter.Or(Filter.Eq(a => a.PausedUntil, null), Filter.Lte(a => a.PausedUntil, when));
        if (exclude.Count > 0)
        {
            filter &= Filter.Nin(a => a.ExternalAccountId, exclude);
        }

        var documents = await _collection.Find(filter)
            .Sort(Builders<ProviderAccountDocument>.Sort.Ascending(a => a.NextPollAt))
            .Limit(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => new ProviderAccount(d.ProviderId, OwnerId.Parse(d.OwnerId), d.ExternalAccountId)).ToList();
    }

    /// <inheritdoc/>
    public Task RecordPollAsync(ProviderAccount account, DateTimeOffset nextPollAt, DateTimeOffset? pausedUntil, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var update = Update.Set(a => a.NextPollAt, UtcTime.ToBson(nextPollAt));
        update = pausedUntil is { } paused ? update.Set(a => a.PausedUntil, UtcTime.ToBson(paused)) : update.Unset(a => a.PausedUntil);
        return _collection.UpdateOneAsync(Key(account), update, cancellationToken: cancellationToken);
    }

    /// <summary>Finds <paramref name="ownerId"/>'s linked account at <paramref name="providerId"/> (D3: at most one).</summary>
    /// <param name="ownerId">The owner.</param>
    /// <param name="providerId">The provider id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The account, or <see langword="null"/> when the owner has not linked one.</returns>
    public async Task<ProviderAccount?> FindAsync(OwnerId ownerId, string providerId, CancellationToken cancellationToken)
    {
        var d = await _collection.Find(Filter.Eq(a => a.OwnerId, ownerId.Value) & Filter.Eq(a => a.ProviderId, providerId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return d is null ? null : new ProviderAccount(d.ProviderId, OwnerId.Parse(d.OwnerId), d.ExternalAccountId);
    }

    private static string Id(ProviderAccount account) => $"{account.ProviderId}/{account.ExternalAccountId}";

    private static FilterDefinition<ProviderAccountDocument> Key(ProviderAccount account) =>
        Filter.Eq(a => a.ProviderId, account.ProviderId) & Filter.Eq(a => a.ExternalAccountId, account.ExternalAccountId);

    private async Task<bool> ExistsAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        await _collection.Find(Key(account)).AnyAsync(cancellationToken).ConfigureAwait(false);

    private static IMongoCollection<ProviderAccountDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<ProviderAccountDocument>(MongoIndexes.ProviderAccounts);
    }
}
