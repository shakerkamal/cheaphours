namespace CheapHours.Persistence.ApiKeys;

/// <summary>
/// An <c>apiKeys</c> document as stored, not validated: the API turns it into its own key record and
/// refuses one that breaks the key rules. Holds the key's SHA-256 hash, never the key (D3).
/// </summary>
/// <param name="KeyHash">The lowercase hex SHA-256 of the key; the document id.</param>
/// <param name="Name">A label for logs and audit.</param>
/// <param name="OwnerId">The owner id as stored (for example <c>own_demo</c>).</param>
/// <param name="Scopes">The scopes as stored.</param>
public sealed record StoredApiKey(string KeyHash, string Name, string OwnerId, IReadOnlyList<string> Scopes)
{
    /// <summary>Describes the key without its hash, so the hash never reaches a log by accident.</summary>
    /// <returns>The name, owner and scopes.</returns>
    public override string ToString() => $"StoredApiKey {{ Name = {Name}, OwnerId = {OwnerId}, Scopes = [{string.Join(", ", Scopes)}] }}";
}
