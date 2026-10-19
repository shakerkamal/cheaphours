namespace CheapHours.Host.Api.Auth;

/// <summary>The <c>ApiKeys</c> configuration section: hashes only, never plaintext keys.</summary>
internal sealed class ApiKeyOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "ApiKeys";

    /// <summary>Gets the configured keys.</summary>
    public IList<ApiKeyEntry> Keys { get; } = [];
}

/// <summary>One configured key.</summary>
internal sealed class ApiKeyEntry
{
    /// <summary>Gets or sets the key's label.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the lowercase hex SHA-256 of the key.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Gets or sets the owner id, for example <c>own_demo</c>.</summary>
    public string Owner { get; set; } = "";

    /// <summary>Gets the scopes.</summary>
    public IList<string> Scopes { get; } = [];
}
