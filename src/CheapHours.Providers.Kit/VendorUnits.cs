using System.Globalization;
using System.Text.RegularExpressions;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Converts vendor units to the core's: kW to W and kWh to Wh (nearest whole unit, halves away from zero)
/// and vendor timestamps to UTC. A timestamp without an offset is refused instead of assuming a zone.
/// </summary>
public static partial class VendorUnits
{
    /// <summary>Converts kilowatts to watts.</summary>
    /// <param name="kilowatts">The power in kW.</param>
    /// <returns>The power in W, rounded to the nearest watt (halves away from zero).</returns>
    /// <exception cref="OverflowException">The result does not fit in an <see cref="int"/>.</exception>
    public static int KilowattsToWatts(decimal kilowatts) =>
        decimal.ToInt32(decimal.Round(kilowatts * 1000m, MidpointRounding.AwayFromZero));

    /// <summary>Converts kilowatt-hours to watt-hours.</summary>
    /// <param name="kilowattHours">The energy in kWh.</param>
    /// <returns>The energy in Wh, rounded to the nearest watt-hour (halves away from zero).</returns>
    /// <exception cref="OverflowException">The result does not fit in a <see cref="long"/>.</exception>
    public static long KilowattHoursToWattHours(decimal kilowattHours) =>
        decimal.ToInt64(decimal.Round(kilowattHours * 1000m, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Reads an ISO 8601 timestamp that carries an explicit offset (<c>Z</c> or <c>±hh:mm</c>) and returns it in UTC.
    /// </summary>
    /// <param name="text">The vendor's timestamp.</param>
    /// <param name="utc">The timestamp in UTC when it was usable.</param>
    /// <returns><see langword="true"/> when the timestamp had an offset and parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseUtc(string? text, out DateTimeOffset utc)
    {
        utc = default;
        if (text is null || !IsoWithOffset().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = parsed.ToUniversalTime();
        return true;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoWithOffset();
}
