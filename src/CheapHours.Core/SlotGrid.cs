namespace CheapHours.Core;

/// <summary>
/// The 15-minute UTC grid that prices, plans and telemetry buckets share (D4). UTC slots are always
/// 15 minutes long; only the number of slots in a local (Europe/Berlin) day varies.
/// </summary>
public static class SlotGrid
{
    /// <summary>The length of one slot.</summary>
    public static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(15);

    /// <summary>Returns whether <paramref name="time"/> is a slot start.</summary>
    /// <param name="time">A UTC time.</param>
    /// <returns><see langword="true"/> on a 15-minute boundary.</returns>
    /// <exception cref="ArgumentException"><paramref name="time"/> is not UTC.</exception>
    public static bool IsAligned(DateTimeOffset time) => Guard.Utc(time).Ticks % SlotLength.Ticks == 0;

    /// <summary>Returns the start of the slot that contains <paramref name="time"/>.</summary>
    /// <param name="time">A UTC time.</param>
    /// <returns>The latest slot start at or before <paramref name="time"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="time"/> is not UTC.</exception>
    public static DateTimeOffset Floor(DateTimeOffset time) =>
        new(Guard.Utc(time).Ticks - (time.Ticks % SlotLength.Ticks), TimeSpan.Zero);

    /// <summary>Returns the earliest slot start at or after <paramref name="time"/>.</summary>
    /// <param name="time">A UTC time.</param>
    /// <returns><paramref name="time"/> itself when aligned, else the next slot start.</returns>
    /// <exception cref="ArgumentException"><paramref name="time"/> is not UTC.</exception>
    public static DateTimeOffset Ceiling(DateTimeOffset time) =>
        IsAligned(time) ? time : Floor(time) + SlotLength;
}
