namespace CheapHours.Host.Api.Auth;

/// <summary>API key scopes and the authorization policies that require them.</summary>
internal static class ApiScopes
{
    /// <summary>Read everything the owner can see; also create goals and draft plans.</summary>
    public const string Read = "read";

    /// <summary>Change devices: confirm plans and send commands. Always held together with <see cref="Read"/>.</summary>
    public const string Execute = "execute";

    /// <summary>The claim type that carries a scope.</summary>
    public const string ClaimType = "scope";

    /// <summary>The claim type that carries the key owner's id.</summary>
    public const string OwnerClaimType = "owner";

    /// <summary>The policy for endpoints that need <see cref="Read"/>.</summary>
    public const string ReadPolicy = "scope:read";

    /// <summary>The policy for endpoints that need <see cref="Execute"/>.</summary>
    public const string ExecutePolicy = "scope:execute";

    /// <summary>Gets the scopes a key may hold.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>([Read, Execute], StringComparer.Ordinal);
}
