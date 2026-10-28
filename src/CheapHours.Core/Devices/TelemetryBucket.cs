namespace CheapHours.Core.Devices;

/// <summary>
/// A device's telemetry over one 15-minute slot of the UTC grid (D4): the energy it delivered in the slot
/// and the last state of charge it reported there.
/// </summary>
public sealed record TelemetryBucket
{
    /// <summary>Creates a bucket.</summary>
    /// <param name="start">The slot start (UTC, on the 15-minute grid).</param>
    /// <param name="energyWh">The energy delivered during the slot in Wh; never negative.</param>
    /// <param name="socPercent">The last state of charge reported in the slot (0–100), or <see langword="null"/> when none was.</param>
    /// <exception cref="ArgumentException">The start is not UTC or not on the grid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The energy is negative or the state of charge is outside 0–100.</exception>
    public TelemetryBucket(DateTimeOffset start, long energyWh, decimal? socPercent = null)
    {
        if (!SlotGrid.IsAligned(start))
        {
            throw new ArgumentException($"A bucket must start on a 15-minute boundary, but starts at {start:O}.", nameof(start));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(energyWh);
        if (socPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(socPercent), socPercent, "State of charge must be between 0 and 100 percent.");
        }

        Start = start;
        EnergyWh = energyWh;
        SocPercent = socPercent;
    }

    /// <summary>Gets the slot start (UTC).</summary>
    public DateTimeOffset Start { get; }

    /// <summary>Gets the slot end (UTC): 15 minutes after the start.</summary>
    public DateTimeOffset End => Start + SlotGrid.SlotLength;

    /// <summary>Gets the energy delivered during the slot in Wh.</summary>
    public long EnergyWh { get; }

    /// <summary>Gets the last state of charge reported in the slot, or <see langword="null"/>.</summary>
    public decimal? SocPercent { get; }

    /// <summary>
    /// Gets the average power over the slot in W: the energy spread over 15 minutes. Capped at
    /// <see cref="int.MaxValue"/>, which only an impossible meter jump can reach.
    /// </summary>
    public int AvgPowerW
    {
        get
        {
            var slotsPerHour = TimeSpan.FromHours(1).Ticks / (decimal)SlotGrid.SlotLength.Ticks;
            var watts = EnergyWh * slotsPerHour;
            return watts >= int.MaxValue ? int.MaxValue : (int)Math.Round(watts, MidpointRounding.AwayFromZero);
        }
    }
}
