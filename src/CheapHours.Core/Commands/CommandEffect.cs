using CheapHours.Core.Devices;

namespace CheapHours.Core.Commands;

/// <summary>
/// The one rule for "the device is in the state this command asks for", shared by confirmation and reconcile:
/// charging for a start; not charging (available or faulted) for a stop; for a power limit, power within the
/// tolerance while charging, or not charging. Offline and unknown never show an effect.
/// </summary>
internal static class CommandEffect
{
    /// <summary>The smallest power-limit tolerance, in W.</summary>
    public const int MinToleranceW = 100;

    /// <summary>The power-limit tolerance as a share of the limit.</summary>
    public const decimal TolerancePercent = 5m;

    public static bool IsShown(DeviceCommand request, DeviceObservation observation) =>
        (request.Type, observation.Status) switch
        {
            (CommandType.StartCharging, DeviceStatus.Charging) => true,
            (CommandType.StopCharging, DeviceStatus.Available or DeviceStatus.Faulted) => true,
            (CommandType.SetPowerLimit, DeviceStatus.Available) => true,
            (CommandType.SetPowerLimit, DeviceStatus.Charging) => IsWithinLimit(request.MaxPowerW, observation.PowerW),
            _ => false,
        };

    // Meters report momentary power with noise: allow max(5% of the limit, 100 W) above it.
    private static bool IsWithinLimit(int? limitW, int? powerW)
    {
        if (limitW is not { } limit || powerW is not { } power)
        {
            return false;
        }

        var tolerance = Math.Max(limit * TolerancePercent / 100m, MinToleranceW);
        return power <= limit + tolerance;
    }
}
