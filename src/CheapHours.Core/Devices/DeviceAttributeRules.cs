namespace CheapHours.Core.Devices;

/// <summary>
/// Which attributes each device type needs (D2): a vehicle needs a battery capacity, a charger a maximum
/// power. <see cref="Device"/> and <see cref="DeviceEvent.Discovered"/> both check with this, so they cannot
/// disagree.
/// </summary>
internal static class DeviceAttributeRules
{
    /// <summary>Throws when <paramref name="attributes"/> lacks what <paramref name="type"/> needs.</summary>
    /// <param name="type">The device type.</param>
    /// <param name="attributes">The attributes to check.</param>
    /// <param name="paramName">The caller's parameter name, for the exception.</param>
    /// <returns>The attributes, unchanged.</returns>
    /// <exception cref="ArgumentException">A required attribute is missing.</exception>
    public static DeviceAttributes Require(DeviceType type, DeviceAttributes attributes, string paramName)
    {
        ArgumentNullException.ThrowIfNull(attributes, paramName);

        if (type == DeviceType.Charger && attributes.MaxPowerW is null)
        {
            throw new ArgumentException("A charger must have a maximum power.", paramName);
        }

        if (type == DeviceType.Vehicle && attributes.BatteryCapacityWh is null)
        {
            throw new ArgumentException("A vehicle must have a battery capacity.", paramName);
        }

        return attributes;
    }
}
