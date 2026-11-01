namespace CheapHours.Providers.Kit;

/// <summary>
/// One linked account to seed. Seeding only inserts a missing account (or re-links one marked
/// reconnect-needed), so a refresh token the vendor rotated is never overwritten by this one.
/// </summary>
public sealed class OAuthAccountOptions
{
    /// <summary>Gets or sets the CheapHours owner id, for example <c>own_demo</c>.</summary>
    public string? Owner { get; set; }

    /// <summary>Gets or sets the vendor's id for the account.</summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Gets or sets the refresh token from the one-time consent; never logged.</summary>
    public string? RefreshToken { get; set; }
}
