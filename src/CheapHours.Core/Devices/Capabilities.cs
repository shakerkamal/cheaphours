namespace CheapHours.Core.Devices;

/// <summary>
/// What a device (through its provider) can do. The numeric values are part of the contract: they are
/// stored and exchanged, so existing values are never renumbered.
/// </summary>
[Flags]
public enum Capabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Reports power and energy readings.</summary>
    ReadTelemetry = 1,

    /// <summary>Can be started and stopped remotely.</summary>
    StartStop = 2,

    /// <summary>Accepts a maximum charging or consumption power.</summary>
    SetPowerLimit = 4,

    /// <summary>Reports a battery's state of charge.</summary>
    ReadStateOfCharge = 8,

    /// <summary>Pushes events to us (OCPP frames, webhooks) instead of being polled.</summary>
    PushesEvents = 16,
}
