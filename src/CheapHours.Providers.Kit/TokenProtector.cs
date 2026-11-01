using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Turns <see cref="OAuthTokens"/> into one protected blob and back, with the data protection key ring
/// (purpose <c>CheapHours.Providers.Tokens.v1</c>). A blob that cannot be read (key ring replaced, tampered)
/// is reported as unreadable, never thrown.
/// </summary>
internal sealed class TokenProtector(IDataProtectionProvider protection)
{
    /// <summary>The data protection purpose; changing it makes every stored token unreadable.</summary>
    public const string Purpose = "CheapHours.Providers.Tokens.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    /// <summary>Protects tokens.</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The protected blob.</returns>
    public string Protect(OAuthTokens tokens) =>
        _protector.Protect(JsonSerializer.Serialize(new Blob(tokens.AccessToken, tokens.AccessExpiresAt, tokens.RefreshToken), Json));

    /// <summary>Reads a protected blob.</summary>
    /// <param name="protectedTokens">The blob.</param>
    /// <param name="tokens">The tokens when readable.</param>
    /// <returns><see langword="true"/> when the blob could be decrypted and read.</returns>
    public bool TryUnprotect(string protectedTokens, out OAuthTokens? tokens)
    {
        tokens = null;
        try
        {
            var blob = JsonSerializer.Deserialize<Blob>(_protector.Unprotect(protectedTokens), Json);
            if (blob is not { RefreshToken: { Length: > 0 } refresh })
            {
                return false;
            }

            tokens = new OAuthTokens(blob.AccessToken, blob.AccessExpiresAt, refresh);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private sealed record Blob(string? AccessToken, DateTimeOffset? AccessExpiresAt, string? RefreshToken);
}
