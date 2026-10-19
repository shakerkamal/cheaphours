namespace CheapHours.Core.Devices;

/// <summary>Typed physical attributes of a device (D2). Which ones are required depends on the device type.</summary>
public sealed record DeviceAttributes
{
    /// <summary>Creates device attributes.</summary>
    /// <param name="batteryCapacityWh">Usable battery capacity in Wh; required for vehicles.</param>
    /// <param name="maxPowerW">Maximum charging power in W; required for chargers.</param>
    public DeviceAttributes(long? batteryCapacityWh = null, int? maxPowerW = null)
    {
        if (batteryCapacityWh is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batteryCapacityWh), batteryCapacityWh, "Battery capacity must be greater than zero.");
        }

        if (maxPowerW is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPowerW), maxPowerW, "Maximum power must be greater than zero.");
        }

        BatteryCapacityWh = batteryCapacityWh;
        MaxPowerW = maxPowerW;
    }

    /// <summary>Gets the usable battery capacity in Wh, if known.</summary>
    public long? BatteryCapacityWh { get; }

    /// <summary>Gets the maximum charging power in W, if known.</summary>
    public int? MaxPowerW { get; }
}
