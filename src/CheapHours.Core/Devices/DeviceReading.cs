namespace CheapHours.Core.Devices;

/// <summary>
/// One normalised measurement from a device: watts, watt-hours, percent and UTC. Providers produce readings
/// keyed by the vendor's own device id; the core maps that to a <see cref="Ids.DeviceId"/>.
/// </summary>
public sealed record DeviceReading
{
    /// <summary>Creates a reading.</summary>
    /// <param name="externalId">The vendor's id for the device (for example an OCPP charge point id).</param>
    /// <param name="measurementTime">When the device took the measurement, in UTC.</param>
    /// <param name="powerW">Instantaneous power in W; negative means the device is feeding power back.</param>
    /// <param name="energyWh">Cumulative energy in Wh (a meter value); never negative.</param>
    /// <param name="socPercent">Battery state of charge in percent (0–100), or <see langword="null"/> when unknown.</param>
    /// <exception cref="ArgumentException">A value breaks one of the rules above.</exception>
    public DeviceReading(string externalId, DateTimeOffset measurementTime, int powerW, long energyWh, decimal? socPercent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(energyWh);
        if (socPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(socPercent), socPercent, "State of charge must be between 0 and 100 percent.");
        }

        ExternalId = Guard.NotBlank(externalId);
        MeasurementTime = Guard.Utc(measurementTime);
        PowerW = powerW;
        EnergyWh = energyWh;
        SocPercent = socPercent;
    }

    /// <summary>Gets the vendor's id for the device.</summary>
    public string ExternalId { get; }

    /// <summary>Gets when the measurement was taken (UTC).</summary>
    public DateTimeOffset MeasurementTime { get; }

    /// <summary>Gets the instantaneous power in W.</summary>
    public int PowerW { get; }

    /// <summary>Gets the cumulative energy in Wh.</summary>
    public long EnergyWh { get; }

    /// <summary>Gets the state of charge in percent, or <see langword="null"/> when unknown (never reported as zero).</summary>
    public decimal? SocPercent { get; }
}
