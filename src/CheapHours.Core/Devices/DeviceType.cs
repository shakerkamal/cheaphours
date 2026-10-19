namespace CheapHours.Core.Devices;

/// <summary>The kind of appliance a device is, independent of its vendor.</summary>
public enum DeviceType
{
    /// <summary>An EV charger (wallbox).</summary>
    Charger,

    /// <summary>An electric vehicle, usually reached through its maker's cloud.</summary>
    Vehicle,

    /// <summary>A heat pump.</summary>
    HeatPump,
}
