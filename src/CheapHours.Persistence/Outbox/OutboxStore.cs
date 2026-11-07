using System.Globalization;
using CheapHours.Core.Ids;
using MongoDB.Driver;

namespace CheapHours.Persistence.Outbox;

/// <summary>
/// The <c>outbox</c> collection (command-dispatch design §2–3). The relay reads due entries and marks them
/// published; writers add and remove entries only inside their own transactions through the internal helpers.
/// </summary>
/// <param name="database">The database.</param>
public sealed class OutboxStore(IMongoDatabase database) : IOutboxStore
{
    private static readonly FilterDefinitionBuilder<OutboxDocument> Filter = Builders<OutboxDocument>.Filter;

    private readonly IMongoCollection<OutboxDocument> _outbox = Collection(database);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxEntry>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var documents = await _outbox.Find(Filter.Eq(d => d.Published, false) & Filter.Lte(d => d.AvailableAt, UtcTime.ToBson(now)))
            .SortBy(d => d.AvailableAt).ThenBy(d => d.CreatedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. documents.Select(ToEntry)];
    }

    /// <inheritdoc/>
    public async Task<bool> MarkPublishedAsync(Guid messageId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var result = await _outbox.UpdateOneAsync(
            Filter.Eq(d => d.Id, IdOf(messageId)) & Filter.Eq(d => d.Published, false),
            Builders<OutboxDocument>.Update.Set(d => d.Published, true).Set(d => d.PublishedAt, UtcTime.ToBson(at)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount > 0;
    }

    /// <inheritdoc/>
    public Task RecordFailedAttemptAsync(Guid messageId, CancellationToken cancellationToken) =>
        _outbox.UpdateOneAsync(
            Filter.Eq(d => d.Id, IdOf(messageId)) & Filter.Eq(d => d.Published, false),
            Builders<OutboxDocument>.Update.Inc(d => d.Attempts, 1),
            cancellationToken: cancellationToken);

    /// <summary>Adds entries inside <paramref name="session"/>'s transaction.</summary>
    internal static Task AddAsync(IMongoCollection<OutboxDocument> outbox, IClientSessionHandle session, IReadOnlyCollection<OutboxEntry> entries, CancellationToken cancellationToken) =>
        entries.Count == 0
            ? Task.CompletedTask
            : outbox.InsertManyAsync(session, entries.Select(ToDocument), cancellationToken: cancellationToken);

    /// <summary>Deletes the unpublished entries of <paramref name="commandIds"/> inside <paramref name="session"/>'s transaction.</summary>
    internal static Task DeleteUnpublishedAsync(IMongoCollection<OutboxDocument> outbox, IClientSessionHandle session, IReadOnlyCollection<CommandId> commandIds, CancellationToken cancellationToken) =>
        commandIds.Count == 0
            ? Task.CompletedTask
            : outbox.DeleteManyAsync(
                session,
                Filter.In(d => d.CommandId, commandIds.Select(c => c.Value)) & Filter.Eq(d => d.Published, false),
                cancellationToken: cancellationToken);

    internal static IMongoCollection<OutboxDocument> Collection(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoSetup.RegisterConventions();
        return database.GetCollection<OutboxDocument>(MongoIndexes.Outbox);
    }

    internal static string IdOf(Guid messageId) => messageId.ToString("D", CultureInfo.InvariantCulture);

    private static OutboxDocument ToDocument(OutboxEntry e) => new()
    {
        Id = IdOf(e.MessageId),
        Type = e.Type,
        MessageSchemaVersion = e.SchemaVersion,
        Body = e.Body,
        Traceparent = e.Traceparent,
        Route = e.Route,
        Destination = e.Destination,
        AvailableAt = UtcTime.ToBson(e.AvailableAt),
        CreatedAt = UtcTime.ToBson(e.CreatedAt),
        Published = e.PublishedAt is not null,
        PublishedAt = e.PublishedAt is { } published ? UtcTime.ToBson(published) : null,
        Attempts = e.Attempts,
        CommandId = e.CommandId?.Value,
    };

    private static OutboxEntry ToEntry(OutboxDocument d) => new(
        Guid.Parse(d.Id, CultureInfo.InvariantCulture),
        d.Type,
        d.MessageSchemaVersion,
        d.Body,
        d.Traceparent,
        d.Route,
        d.Destination,
        UtcTime.FromBson(d.AvailableAt),
        UtcTime.FromBson(d.CreatedAt),
        d.CommandId is { } command ? Core.Ids.CommandId.Parse(command) : null,
        UtcTime.FromBson(d.PublishedAt),
        d.Attempts);
}
