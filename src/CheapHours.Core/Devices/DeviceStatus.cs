namespace CheapHours.Core.Devices;

/// <summary>A device's last reported operating status, normalised across vendors.</summary>
public enum DeviceStatus
{
    /// <summary>Not yet reported, or the vendor's status has no mapping.</summary>
    Unknown,

    /// <summary>Connected and ready, not delivering power.</summary>
    Available,

    /// <summary>Delivering or drawing power.</summary>
    Charging,

    /// <summary>Reports a fault.</summary>
    Faulted,

    /// <summary>Not reachable (for example three missed heartbeats).</summary>
    Offline,
}
