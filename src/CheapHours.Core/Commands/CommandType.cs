namespace CheapHours.Core.Commands;

/// <summary>The closed set of things CheapHours can ask a device to do (D5: per-slot commands).</summary>
public enum CommandType
{
    /// <summary>Start charging, optionally at a maximum power.</summary>
    StartCharging,

    /// <summary>Stop charging.</summary>
    StopCharging,

    /// <summary>Change the maximum power while charging.</summary>
    SetPowerLimit,
}
