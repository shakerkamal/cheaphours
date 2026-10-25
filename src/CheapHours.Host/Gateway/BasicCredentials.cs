using System.Text;

namespace CheapHours.Host.Gateway;

/// <summary>Reads an HTTP basic-auth header (<c>Basic base64(username:password)</c>).</summary>
internal static class BasicCredentials
{
    /// <summary>The failure category for a request without credentials.</summary>
    public const string Missing = "missing";

    /// <summary>The failure category for credentials that cannot be read.</summary>
    public const string Malformed = "malformed";

    private const string Scheme = "Basic ";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parses the header. The password may contain colons; the username may not.</summary>
    /// <param name="header">The <c>Authorization</c> header value, if any.</param>
    /// <param name="username">The username when this returns <see langword="true"/>.</param>
    /// <param name="password">The password when this returns <see langword="true"/>. Never log it.</param>
    /// <param name="failure"><see cref="Missing"/> or <see cref="Malformed"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when a non-empty username and password were read.</returns>
    public static bool TryParse(string? header, out string username, out string password, out string failure)
    {
        username = string.Empty;
        password = string.Empty;

        if (string.IsNullOrWhiteSpace(header))
        {
            failure = Missing;
            return false;
        }

        failure = Malformed;
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(header[Scheme.Length..].Trim()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == decoded.Length - 1)
        {
            return false;
        }

        username = decoded[..colon];
        password = decoded[(colon + 1)..];
        failure = string.Empty;
        return true;
    }
}
