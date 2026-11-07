namespace CheapHours.Core.Commands;

/// <summary>A trigger was applied to a command in a state that does not allow it. The command is unchanged.</summary>
public sealed class InvalidCommandTransitionException : InvalidOperationException
{
    /// <summary>Creates the exception for a refused trigger.</summary>
    /// <param name="state">The state the command was in.</param>
    /// <param name="trigger">The refused trigger.</param>
    public InvalidCommandTransitionException(CommandState state, CommandTrigger trigger)
        : base($"A command in state {state} cannot take trigger {trigger}.")
    {
        State = state;
        Trigger = trigger;
    }

    /// <summary>Creates the exception for a stored history that moves between two states no trigger connects.</summary>
    /// <param name="state">The state the command was in.</param>
    /// <param name="targetState">The state the history claims came next.</param>
    public InvalidCommandTransitionException(CommandState state, CommandState targetState)
        : base($"A command in state {state} cannot move to {targetState}.")
    {
        State = state;
        TargetState = targetState;
    }

    /// <summary>Gets the state the command was in.</summary>
    public CommandState State { get; }

    /// <summary>Gets the refused trigger, when a trigger was applied.</summary>
    public CommandTrigger? Trigger { get; }

    /// <summary>Gets the state a stored history claimed came next, when one was replayed.</summary>
    public CommandState? TargetState { get; }
}
