using CheapHours.Core.Devices;

namespace CheapHours.Core.Commands;

/// <summary>What the device last reported, used to reconcile a command that was never confirmed.</summary>
public sealed record DeviceObservation
{
    /// <summary>Creates an observation.</summary>
    /// <param name="status">The reported status.</param>
    /// <param name="powerW">The reported power in W, if the device reports it.</param>
    /// <param name="observedAt">When the device reported it (UTC).</param>
    /// <exception cref="ArgumentException">The status is unknown to the enum, or the time is not UTC.</exception>
    public DeviceObservation(DeviceStatus status, int? powerW, DateTimeOffset observedAt)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown device status.");
        }

        Status = status;
        PowerW = powerW;
        ObservedAt = Guard.Utc(observedAt);
    }

    /// <summary>Gets the reported status.</summary>
    public DeviceStatus Status { get; }

    /// <summary>Gets the reported power in W, if any.</summary>
    public int? PowerW { get; }

    /// <summary>Gets when the device reported it (UTC).</summary>
    public DateTimeOffset ObservedAt { get; }
}
