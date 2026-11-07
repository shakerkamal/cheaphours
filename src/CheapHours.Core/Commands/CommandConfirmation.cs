namespace CheapHours.Core.Commands;

/// <summary>
/// Decides whether what the device last reported confirms a sent command. The device is the source of truth:
/// the vendor accepting a command never confirms it, and only a report received after the send counts,
/// unless the route said the device was already in the requested state.
/// </summary>
public static class CommandConfirmation
{
    /// <summary>Returns whether <paramref name="observation"/> confirms <paramref name="command"/>.</summary>
    /// <param name="command">A command in <see cref="CommandState.Sent"/>.</param>
    /// <param name="observation">What the device last reported, or <see langword="null"/> if nothing is known.</param>
    /// <param name="alreadyInEffect">Whether the route answered that the device was already in the requested state without sending; a matching report from before the send then counts.</param>
    /// <returns><see langword="true"/> if the command should move to <see cref="CommandState.Confirmed"/>.</returns>
    /// <exception cref="ArgumentException">The command is not sent.</exception>
    public static bool IsConfirmed(Command command, DeviceObservation? observation, bool alreadyInEffect)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.State != CommandState.Sent)
        {
            throw new ArgumentException($"Only a sent command can be confirmed, but this one is {command.State}.", nameof(command));
        }

        if (observation is null)
        {
            return false;
        }

        var fresh = observation.ObservedAt > command.LastSentAt;
        return (fresh || alreadyInEffect) && CommandEffect.IsShown(command.Request, observation);
    }
}
