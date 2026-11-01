using System.Security.Cryptography;
using System.Text;

namespace CheapHours.Host.Devices;

/// <summary>
/// Name-based ids: SHA-256 of the name, cut to 16 bytes, with the version (8) and variant bits of RFC 9562
/// (UUIDv8). The same name always gives the same id, so a repeated poll's telemetry message is a duplicate the
/// inbox drops.
/// </summary>
internal static class DeterministicGuid
{
    /// <summary>Returns the id for <paramref name="name"/>.</summary>
    /// <param name="name">The name, for example the provider, the vendor's device id and the reading times.</param>
    /// <returns>A version-8 UUID.</returns>
    public static Guid From(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10 (RFC 9562)
        return new Guid(bytes, bigEndian: true);
    }
}
