namespace CheapHours.Providers.Kit;

/// <summary>An account's tokens as stored: an opaque protected blob and its version.</summary>
/// <param name="ProtectedTokens">The tokens, protected with the data protection key ring.</param>
/// <param name="Version">Incremented on every write; used for compare-and-set.</param>
/// <param name="ReconnectNeeded">Whether the account is marked as needing a new link.</param>
public sealed record StoredAccountTokens(string ProtectedTokens, long Version, bool ReconnectNeeded)
{
    /// <summary>Keeps the protected blob out of logs and exception messages.</summary>
    /// <returns>The version and the mark only.</returns>
    public override string ToString() => $"StoredAccountTokens {{ Version = {Version}, ReconnectNeeded = {ReconnectNeeded} }}";
}
