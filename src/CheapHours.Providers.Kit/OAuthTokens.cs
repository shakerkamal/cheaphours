namespace CheapHours.Providers.Kit;

/// <summary>An account's decrypted tokens. Lives in memory only; stored protected by <see cref="TokenProtector"/>.</summary>
/// <param name="AccessToken">The access token, or <see langword="null"/> before the first refresh (a seed has only a refresh token).</param>
/// <param name="AccessExpiresAt">When the access token expires (UTC), if known.</param>
/// <param name="RefreshToken">The current refresh token.</param>
internal sealed record OAuthTokens(string? AccessToken, DateTimeOffset? AccessExpiresAt, string RefreshToken)
{
    /// <summary>Whether the access token can be used at <paramref name="now"/>, keeping <paramref name="margin"/> before expiry.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="margin">How long before expiry a token is refreshed.</param>
    /// <returns><see langword="true"/> when there is an access token that does not expire within the margin.</returns>
    public bool IsUsable(DateTimeOffset now, TimeSpan margin) =>
        AccessToken is not null && AccessExpiresAt is { } expires && expires - margin > now;

    /// <summary>Keeps tokens out of logs and exception messages.</summary>
    /// <returns>The expiry only.</returns>
    public override string ToString() => $"OAuthTokens {{ AccessExpiresAt = {AccessExpiresAt:O} }}";
}
