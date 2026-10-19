using System.Buffers.Binary;

namespace CheapHours.Core.Ids;

/// <summary>Shared parsing, validation and generation for the prefixed id types.</summary>
public static class PrefixedId
{
    /// <summary>The longest body (the part after the prefix) an id may have.</summary>
    public const int MaxBodyLength = 64;

    private const string _crockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int _generatedBodyLength = 26;

    /// <summary>Parses <paramref name="s"/> as a <typeparamref name="TId"/>.</summary>
    /// <typeparam name="TId">The id type.</typeparam>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is wrong or missing, or the body is empty or invalid.</exception>
    public static TId Parse<TId>(string s) where TId : struct, IPrefixedId<TId> =>
        TId.TryParse(s, out var id)
            ? id
            : throw new FormatException(
                $"'{s}' is not a valid {typeof(TId).Name}: expected the prefix '{TId.Prefix}' followed by 1–{MaxBodyLength} characters from [A-Za-z0-9_-].");

    /// <summary>Returns whether <paramref name="s"/> is <paramref name="prefix"/> followed by a valid body.</summary>
    /// <param name="s">The candidate id.</param>
    /// <param name="prefix">The required prefix.</param>
    /// <returns><see langword="true"/> when the id is well formed.</returns>
    public static bool IsValid(string? s, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (s is null || !s.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = s.AsSpan(prefix.Length);
        if (body.IsEmpty || body.Length > MaxBodyLength)
        {
            return false;
        }

        foreach (var c in body)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a new id value: <paramref name="prefix"/> followed by a UUIDv7 for the current time, encoded
    /// as 26 Crockford base32 characters, so ids sort by creation time.
    /// </summary>
    /// <param name="prefix">The id prefix.</param>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id string.</returns>
    public static string NewValue(string prefix, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(time);

        Span<byte> bytes = stackalloc byte[16];
        if (!Guid.CreateVersion7(time.GetUtcNow()).TryWriteBytes(bytes, bigEndian: true, out _))
        {
            throw new InvalidOperationException("Could not write the UUID bytes.");
        }

        var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> body = stackalloc char[_generatedBodyLength];
        for (var i = _generatedBodyLength - 1; i >= 0; i--)
        {
            body[i] = _crockfordAlphabet[(int)(value & 31)];
            value >>= 5;
        }

        return string.Concat(prefix, body);
    }
}
