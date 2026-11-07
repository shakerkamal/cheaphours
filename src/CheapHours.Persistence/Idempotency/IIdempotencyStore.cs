namespace CheapHours.Persistence.Idempotency;

/// <summary>Idempotency records: per owner and key, the request it was used for and its response, for 24 hours.</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Starts a request under <paramref name="id"/>: records it as in progress, or says how to answer a repeat.
    /// An expired record counts as absent; an in-progress record older than <paramref name="abandonAfter"/> is
    /// taken over.
    /// </summary>
    /// <param name="id">The owner and key, <c>{ownerId}|{key}</c>.</param>
    /// <param name="fingerprint">The request's fingerprint.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="keepFor">How long the record is kept.</param>
    /// <param name="abandonAfter">How long an in-progress request may take before it counts as abandoned.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What to do with the request.</returns>
    Task<IdempotencyClaim> StartAsync(string id, string fingerprint, DateTimeOffset now, TimeSpan keepFor, TimeSpan abandonAfter, CancellationToken cancellationToken);

    /// <summary>Stores the response of the request started under <paramref name="id"/>.</summary>
    /// <param name="id">The owner and key.</param>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task CompleteAsync(string id, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Forgets the in-progress request under <paramref name="id"/>, so the client can retry with the key.</summary>
    /// <param name="id">The owner and key.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when deleted.</returns>
    Task ForgetAsync(string id, CancellationToken cancellationToken);
}
