using CheapHours.Core.Ids;
using CheapHours.Persistence.ApiKeys;

namespace CheapHours.Host.Api.Auth;

/// <summary>
/// API keys from the <c>apiKeys</c> collection (D3). A stored key that breaks the key rules counts as
/// unknown, so the request gets 401; the log names the key and the rule it broke, never its hash.
/// </summary>
/// <param name="keys">The stored keys.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class MongoApiKeyStore(IStoredApiKeys keys, ILogger<MongoApiKeyStore> logger) : IApiKeyStore
{
    /// <inheritdoc/>
    public async ValueTask<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken)
    {
        var stored = await keys.FindByHashAsync(keyHash, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        if (!OwnerId.TryParse(stored.OwnerId, out var owner))
        {
            LogMalformed(stored.Name, $"the owner '{stored.OwnerId}' is not a valid owner id.");
            return null;
        }

        try
        {
            return new ApiKeyRecord(stored.Name, stored.KeyHash, owner, stored.Scopes.ToArray());
        }
        catch (ArgumentException ex)
        {
            // The record's rule messages name the key and the rule, never the hash.
            LogMalformed(stored.Name, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored API key '{KeyName}' was refused because it breaks the key rules: {Reason}")]
    private partial void LogMalformed(string keyName, string reason);
}
