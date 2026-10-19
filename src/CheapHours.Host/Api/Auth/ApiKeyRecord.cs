using CheapHours.Core.Ids;

namespace CheapHours.Host.Api.Auth;

/// <summary>A stored API key: its SHA-256 hash, never the key itself (D3).</summary>
internal sealed record ApiKeyRecord
{
    /// <summary>Creates a key record.</summary>
    /// <param name="name">A label for the key, for logs and audit (never the key).</param>
    /// <param name="keyHash">The lowercase hex SHA-256 of the key.</param>
    /// <param name="ownerId">Whose devices, goals and plans the key can reach.</param>
    /// <param name="scopes">The scopes; must include <c>read</c>, and may add <c>execute</c>.</param>
    /// <exception cref="ArgumentException">The hash is malformed, or the scopes are unknown or lack <c>read</c>.</exception>
    public ApiKeyRecord(string name, string keyHash, OwnerId ownerId, IReadOnlyCollection<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scopes);
        if (!ApiKeyHasher.IsHash(keyHash))
        {
            throw new ArgumentException($"API key '{name}': the hash must be 64 lowercase hex characters (SHA-256).", nameof(keyHash));
        }

        if (scopes.FirstOrDefault(s => !ApiScopes.Known.Contains(s)) is { } unknown)
        {
            throw new ArgumentException($"API key '{name}': unknown scope '{unknown}'.", nameof(scopes));
        }

        if (!scopes.Contains(ApiScopes.Read))
        {
            throw new ArgumentException($"API key '{name}': every key must hold the 'read' scope.", nameof(scopes));
        }

        if (ownerId.Equals(default(OwnerId)))
        {
            throw new ArgumentException($"API key '{name}': an owner is required.", nameof(ownerId));
        }

        Name = name;
        KeyHash = keyHash;
        OwnerId = ownerId;
        Scopes = new HashSet<string>(scopes, StringComparer.Ordinal);
    }

    /// <summary>Gets the key's label.</summary>
    public string Name { get; }

    /// <summary>Gets the key's SHA-256 hash.</summary>
    public string KeyHash { get; }

    /// <summary>Gets the owner.</summary>
    public OwnerId OwnerId { get; }

    /// <summary>Gets the scopes.</summary>
    public IReadOnlySet<string> Scopes { get; }
}
