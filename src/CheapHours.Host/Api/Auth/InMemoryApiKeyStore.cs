using CheapHours.Core.Ids;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Api.Auth;

/// <summary>API keys read from configuration (section <c>ApiKeys</c>), holding hashes only.</summary>
internal sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, ApiKeyRecord> _byHash;

    /// <summary>Loads and validates the configured keys.</summary>
    /// <param name="options">The configured keys.</param>
    /// <exception cref="ArgumentException">A configured key is invalid.</exception>
    /// <exception cref="InvalidOperationException">Two keys share a hash.</exception>
    public InMemoryApiKeyStore(IOptions<ApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _byHash = new Dictionary<string, ApiKeyRecord>(StringComparer.Ordinal);
        foreach (var entry in options.Value.Keys)
        {
            var record = new ApiKeyRecord(entry.Name, entry.Hash, OwnerId.Parse(entry.Owner), entry.Scopes.ToArray());
            if (!_byHash.TryAdd(record.KeyHash, record))
            {
                throw new InvalidOperationException($"API key '{record.Name}' has the same hash as '{_byHash[record.KeyHash].Name}'.");
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_byHash.GetValueOrDefault(keyHash));
}
