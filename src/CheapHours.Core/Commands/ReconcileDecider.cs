using CheapHours.Core.Devices;

namespace CheapHours.Core.Commands;

/// <summary>
/// Decides what happens to a command that the device did not confirm in time, from what the device last
/// reported. The device is the source of truth: only an observation made after the last send can confirm.
/// </summary>
public static class ReconcileDecider
{
    /// <summary>The smallest power-limit tolerance, in W.</summary>
    public const int MinToleranceW = 100;

    /// <summary>The power-limit tolerance as a share of the limit.</summary>
    public const decimal TolerancePercent = 5m;

    private static readonly ReconcileDecision.Confirm Confirm = new();
    private static readonly ReconcileDecision.Resend Resend = new();

    /// <summary>Decides confirm, resend or fail.</summary>
    /// <param name="command">A command in <see cref="CommandState.Reconciling"/>.</param>
    /// <param name="observation">What the device last reported, or <see langword="null"/> if nothing is known.</param>
    /// <param name="options">The retry numbers; a resend becomes a fail once <see cref="RetryPolicyOptions.MaxAttempts"/> are used.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentException">The command is not reconciling.</exception>
    public static ReconcileDecision Decide(Command command, DeviceObservation? observation, RetryPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        if (command.State != CommandState.Reconciling)
        {
            throw new ArgumentException($"Only a reconciling command can be decided, but this one is {command.State}.", nameof(command));
        }

        // A reading from before (or at) the last send cannot show the effect of that send.
        var fresh = observation is not null && observation.ObservedAt > command.LastSentAt ? observation : null;
        var status = fresh?.Status ?? DeviceStatus.Unknown;
        var type = command.Request.Type;

        ReconcileDecision decision = (type, status) switch
        {
            (_, DeviceStatus.Offline or DeviceStatus.Unknown) => Resend,
            (CommandType.StartCharging, DeviceStatus.Charging) => Confirm,
            (CommandType.StartCharging, DeviceStatus.Available) => Resend,
            (CommandType.StopCharging, DeviceStatus.Charging) => Resend,
            (CommandType.StopCharging, DeviceStatus.Available or DeviceStatus.Faulted) => Confirm,
            (CommandType.SetPowerLimit, DeviceStatus.Charging) => IsWithinLimit(command.Request.MaxPowerW, fresh?.PowerW) ? Confirm : Resend,
            (CommandType.SetPowerLimit, DeviceStatus.Available) => Confirm,
            (CommandType.StartCharging or CommandType.SetPowerLimit, DeviceStatus.Faulted) =>
                new ReconcileDecision.Fail($"Device reported {status}; {type} cannot take effect."),
            _ => throw new ArgumentOutOfRangeException(nameof(command), (type, status), "No reconcile rule for this command type and status."),
        };

        return decision is ReconcileDecision.Resend && command.Attempts >= options.MaxAttempts
            ? new ReconcileDecision.Fail($"Not confirmed and no attempts left after {command.Attempts}.")
            : decision;
    }

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
