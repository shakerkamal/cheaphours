using System.Globalization;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Reads a vendor's <c>Retry-After</c>: delta seconds or an HTTP date, the date taken relative to the
/// injected clock. Values above one hour are capped at one hour; past or unparseable values are ignored.
/// </summary>
public static class RetryAfterParser
{
    /// <summary>The longest pause a vendor can ask for.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromHours(1);

    /// <summary>Reads the <c>Retry-After</c> header of a response.</summary>
    /// <param name="response">The vendor's response.</param>
    /// <param name="time">The clock, for HTTP dates.</param>
    /// <returns>The pause, or <see langword="null"/> when the header is absent or unusable.</returns>
    public static TimeSpan? From(HttpResponseMessage response, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(time);

        return response.Headers.TryGetValues("Retry-After", out var values)
            ? Parse(values.FirstOrDefault(), time.GetUtcNow())
            : null;
    }

    /// <summary>Reads a <c>Retry-After</c> value.</summary>
    /// <param name="value">The header value: delta seconds or an HTTP date (RFC 1123).</param>
    /// <param name="now">The current time (UTC), for HTTP dates.</param>
    /// <returns>The pause, capped at <see cref="Cap"/>, or <see langword="null"/> when unusable.</returns>
    public static TimeSpan? Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.All(char.IsAsciiDigit))
        {
            // Too many digits for a long is still a valid "wait very long": cap it.
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds < Cap.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : Cap;
        }

        if (!DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return null;
        }

        var delay = date - now;
        if (delay < TimeSpan.Zero)
        {
            return null;
        }

        return delay > Cap ? Cap : delay;
    }
}
