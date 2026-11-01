namespace CheapHours.Providers.Kit;

/// <summary>
/// Options for an HTTP provider whose vendor uses OAuth 2.0 with the refresh-token grant. The kit refreshes
/// access tokens, stores them encrypted and attaches them to every call. The client secret and the seed
/// refresh tokens belong in user secrets, never in a committed file.
/// </summary>
public class OAuthProviderOptions : HttpProviderOptions
{
    /// <summary>Gets or sets the vendor's token endpoint; required and absolute.</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>Gets or sets our client id at the vendor; required.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets our client secret at the vendor; required, never logged.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Gets the linked accounts to seed (D3: one consent done once, then refresh tokens only).</summary>
    public IList<OAuthAccountOptions> Accounts { get; } = [];
}
