using System.Buffers.Text;
using System.Globalization;
using System.Text;
using CheapHours.Core;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api;

/// <summary>
/// Opaque paging cursors: base64url of a short versioned string. They hold only a position the client
/// could ask for anyway, so they are not signed; the owner filter applies regardless.
/// </summary>
internal static class PageCursor
{
    /// <summary>Longer strings are refused before decoding.</summary>
    public const int MaxLength = 200;

    private const string DevicePrefix = "d1:";
    private const string BucketPrefix = "t1:";

    /// <summary>Encodes "continue after this device".</summary>
    /// <param name="after">The last device on the page.</param>
    /// <returns>The cursor.</returns>
    public static string ForDevice(DeviceId after) => Encode(DevicePrefix + after.Value);

    /// <summary>Encodes "continue at the slot after this bucket".</summary>
    /// <param name="lastStart">The start of the last bucket on the page (UTC, on the grid).</param>
    /// <returns>The cursor.</returns>
    public static string ForBucket(DateTimeOffset lastStart) =>
        Encode(BucketPrefix + lastStart.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>Reads a device cursor. Never throws.</summary>
    /// <param name="cursor">The cursor the client sent.</param>
    /// <param name="after">The device to continue after.</param>
    /// <returns><see langword="true"/> when the cursor is a valid device cursor.</returns>
    public static bool TryReadDevice(string? cursor, out DeviceId after)
    {
        after = default;
        return TryDecode(cursor, DevicePrefix, out var body) && DeviceId.TryParse(body, out after);
    }

    /// <summary>Reads a telemetry cursor. Never throws.</summary>
    /// <param name="cursor">The cursor the client sent.</param>
    /// <param name="lastStart">The start of the last bucket the client has seen (UTC, on the grid).</param>
    /// <returns><see langword="true"/> when the cursor is a valid telemetry cursor on the 15-minute grid.</returns>
    public static bool TryReadBucket(string? cursor, out DateTimeOffset lastStart)
    {
        lastStart = default;
        if (!TryDecode(cursor, BucketPrefix, out var body)
            || !long.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var start = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (!SlotGrid.IsAligned(start))
        {
            return false;
        }

        lastStart = start;
        return true;
    }

    private static string Encode(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static bool TryDecode(string? cursor, string prefix, out string body)
    {
        body = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength || !Base64Url.IsValid(cursor))
        {
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        body = text[prefix.Length..];
        return true;
    }
}
