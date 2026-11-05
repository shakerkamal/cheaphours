namespace CheapHours.Host.Prices;

/// <summary>
/// Europe/Berlin calendar days, which is how the day-ahead market and the price source count days. A day runs
/// from local midnight to the next local midnight, so it has 92, 96 or 100 UTC slots. Times stay UTC
/// everywhere else; this is only for finding day bounds and the schedule's local hours.
/// </summary>
internal static class BerlinTime
{
    /// <summary>Gets the Europe/Berlin time zone.</summary>
    public static TimeZoneInfo Zone { get; } = FindZone();

    /// <summary>Returns the local calendar day that contains <paramref name="utc"/>.</summary>
    /// <param name="utc">A UTC time.</param>
    /// <returns>The Berlin day.</returns>
    public static DateOnly DayOf(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc).DateTime);

    /// <summary>Returns <paramref name="utc"/> as Berlin local time.</summary>
    /// <param name="utc">A UTC time.</param>
    /// <returns>The same instant with the Berlin offset.</returns>
    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Returns the UTC instant of local midnight at the start of <paramref name="day"/>.</summary>
    /// <param name="day">A Berlin day.</param>
    /// <returns>The day's first slot start (UTC).</returns>
    public static DateTimeOffset DayStartUtc(DateOnly day) => AtLocal(day, TimeOnly.MinValue);

    /// <summary>Returns the UTC instant of local midnight at the end of <paramref name="day"/>.</summary>
    /// <param name="day">A Berlin day.</param>
    /// <returns>The next day's start (UTC).</returns>
    public static DateTimeOffset DayEndUtc(DateOnly day) => DayStartUtc(day.AddDays(1));

    /// <summary>Returns the number of 15-minute slots in <paramref name="day"/>: 92, 96 or 100.</summary>
    /// <param name="day">A Berlin day.</param>
    /// <returns>The slot count.</returns>
    public static int SlotCount(DateOnly day) => (int)((DayEndUtc(day) - DayStartUtc(day)).Ticks / TimeSpan.FromMinutes(15).Ticks);

    /// <summary>
    /// Returns the UTC instant of <paramref name="time"/> on <paramref name="day"/> in Berlin. A time skipped by
    /// the spring change counts as the moment the clock jumps; a repeated autumn time counts as its first pass.
    /// </summary>
    /// <param name="day">A Berlin day.</param>
    /// <param name="time">A local time of day.</param>
    /// <returns>The UTC instant.</returns>
    public static DateTimeOffset AtLocal(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    // With invariant globalization Windows cannot map the IANA id to its own, so fall back to the Windows id.
    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }
}
