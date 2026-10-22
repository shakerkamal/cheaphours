namespace CheapHours.Core.Commands;

/// <summary>A trigger was applied to a command in a state that does not allow it. The command is unchanged.</summary>
public sealed class InvalidCommandTransitionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="state">The state the command was in.</param>
    /// <param name="trigger">The refused trigger.</param>
    public InvalidCommandTransitionException(CommandState state, CommandTrigger trigger)
        : base($"A command in state {state} cannot take trigger {trigger}.")
    {
        State = state;
        Trigger = trigger;
    }

    /// <summary>Gets the state the command was in.</summary>
    public CommandState State { get; }

    /// <summary>Gets the refused trigger.</summary>
    public CommandTrigger Trigger { get; }
}
