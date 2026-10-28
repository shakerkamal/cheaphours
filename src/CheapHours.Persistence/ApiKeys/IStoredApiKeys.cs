namespace CheapHours.Persistence.ApiKeys;

/// <summary>The <c>apiKeys</c> collection: API keys stored by their SHA-256 hash, never the key itself (D3).</summary>
public interface IStoredApiKeys
{
    /// <summary>Finds the key stored under <paramref name="keyHash"/>, exactly as stored (not validated).</summary>
    /// <param name="keyHash">The lowercase hex SHA-256 of the presented key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored key, or <see langword="null"/> when none has that hash.</returns>
    Task<StoredApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a configured key: creates it, or replaces the name, owner and scopes of the key with the same
    /// hash and keeps its creation time. Keys not passed here are left alone.
    /// </summary>
    /// <param name="key">The key; validated by the caller.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task SeedAsync(StoredApiKey key, DateTimeOffset now, CancellationToken cancellationToken);
}
