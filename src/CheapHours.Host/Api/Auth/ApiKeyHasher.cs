using System.Security.Cryptography;
using System.Text;

namespace CheapHours.Host.Api.Auth;

/// <summary>
/// Hashes API keys for storage and lookup. SHA-256 rather than a slow password hash: keys are 256-bit random
/// values, not user-chosen passwords, so a slow KDF would add latency to every request for no gain.
/// </summary>
internal static class ApiKeyHasher
{
    /// <summary>Returns the lowercase hex SHA-256 of <paramref name="apiKey"/>.</summary>
    /// <param name="apiKey">The key as the client sent it.</param>
    /// <returns>64 lowercase hex characters.</returns>
    public static string Hash(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }

    /// <summary>Returns whether <paramref name="value"/> looks like a hash produced by <see cref="Hash"/>.</summary>
    /// <param name="value">The candidate hash.</param>
    /// <returns><see langword="true"/> for 64 lowercase hex characters.</returns>
    public static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
