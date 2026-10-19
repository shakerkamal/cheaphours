namespace CheapHours.Core.Goals;

/// <summary>One 15-minute market slot in a plan (D4), charged at a fixed power.</summary>
public sealed record PlanSlot
{
    /// <summary>The length of every slot: the day-ahead market time unit since 1 October 2025.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    /// <summary>Creates a slot.</summary>
    /// <param name="startUtc">The slot start in UTC, on a 15-minute boundary.</param>
    /// <param name="powerW">The charging power during the slot, in W; greater than zero.</param>
    /// <exception cref="ArgumentException">The start is not UTC or not on the 15-minute grid, or the power is not positive.</exception>
    public PlanSlot(DateTimeOffset startUtc, int powerW)
    {
        Guard.Utc(startUtc);
        if (startUtc.Ticks % Duration.Ticks != 0)
        {
            throw new ArgumentException($"A slot must start on a 15-minute boundary, but starts at {startUtc:O}.", nameof(startUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(powerW);

        StartUtc = startUtc;
        PowerW = powerW;
    }

    /// <summary>Gets the slot start (UTC).</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>Gets the slot end (UTC).</summary>
    public DateTimeOffset EndUtc => StartUtc + Duration;

    /// <summary>Gets the charging power in W.</summary>
    public int PowerW { get; }
}
