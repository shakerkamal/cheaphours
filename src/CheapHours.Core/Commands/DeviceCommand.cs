using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Core.Commands;

/// <summary>
/// A request for a device to act. Sending it twice with the same <see cref="IdempotencyKey"/> must not make
/// the device act twice.
/// </summary>
public sealed record DeviceCommand
{
    /// <summary>Creates a command.</summary>
    /// <param name="id">The command's id.</param>
    /// <param name="deviceId">The device that must act.</param>
    /// <param name="type">What the device must do.</param>
    /// <param name="idempotencyKey">The key that makes repeated sends safe, for example <c>plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging</c>.</param>
    /// <param name="maxPowerW">Maximum power in W: required (and positive) for <see cref="CommandType.SetPowerLimit"/>, optional for start, not allowed for stop.</param>
    /// <exception cref="ArgumentException">A value is missing or breaks a rule above.</exception>
    public DeviceCommand(CommandId id, DeviceId deviceId, CommandType type, string idempotencyKey, int? maxPowerW = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown command type.");
        }

        if (maxPowerW is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPowerW), maxPowerW, "Maximum power must be greater than zero.");
        }

        if (type == CommandType.SetPowerLimit && maxPowerW is null)
        {
            throw new ArgumentException("SetPowerLimit needs a maximum power.", nameof(maxPowerW));
        }

        if (type == CommandType.StopCharging && maxPowerW is not null)
        {
            throw new ArgumentException("StopCharging takes no maximum power.", nameof(maxPowerW));
        }

        Id = Guard.NotDefault(id);
        DeviceId = Guard.NotDefault(deviceId);
        Type = type;
        IdempotencyKey = Guard.NotBlank(idempotencyKey);
        MaxPowerW = maxPowerW;
    }

    /// <summary>Gets the command's id.</summary>
    public CommandId Id { get; }

    /// <summary>Gets the device that must act.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Gets what the device must do.</summary>
    public CommandType Type { get; }

    /// <summary>Gets the key that makes repeated sends safe.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Gets the maximum power in W, if any.</summary>
    public int? MaxPowerW { get; }

    /// <summary>Gets the capability a provider must declare to carry out this command.</summary>
    public Capabilities RequiredCapability => Type switch
    {
        CommandType.SetPowerLimit => Capabilities.SetPowerLimit,
        _ => Capabilities.StartStop,
    };
}
