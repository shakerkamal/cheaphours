namespace CheapHours.Host.Api.Auth;

/// <summary>Looks API keys up by hash (the product uses <see cref="MongoApiKeyStore"/>).</summary>
internal interface IApiKeyStore
{
    /// <summary>Finds the key whose SHA-256 hash is <paramref name="keyHash"/>.</summary>
    /// <param name="keyHash">The lowercase hex SHA-256 of the presented key.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The key record, or <see langword="null"/> when no such key exists.</returns>
    ValueTask<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken);
}
