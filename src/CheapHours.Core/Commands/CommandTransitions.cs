using System.Collections.Frozen;

namespace CheapHours.Core.Commands;

/// <summary>The command state machine's transition table (D7): the only state changes a command may make.</summary>
public static class CommandTransitions
{
    /// <summary>
    /// Gets the allowed transitions: ten state changes plus the <see cref="CommandTrigger.RetryLater"/>
    /// self-transition on <see cref="CommandState.Published"/>. Any pair not listed is refused.
    /// </summary>
    public static IReadOnlyDictionary<(CommandState From, CommandTrigger Trigger), CommandState> Table { get; } =
        new Dictionary<(CommandState From, CommandTrigger Trigger), CommandState>
        {
            [(CommandState.Pending, CommandTrigger.Publish)] = CommandState.Published,
            [(CommandState.Published, CommandTrigger.Accept)] = CommandState.Sent,
            [(CommandState.Published, CommandTrigger.Reject)] = CommandState.Rejected,
            [(CommandState.Published, CommandTrigger.RetryLater)] = CommandState.Published,
            [(CommandState.Published, CommandTrigger.GiveUp)] = CommandState.DeadLettered,
            [(CommandState.Sent, CommandTrigger.Confirm)] = CommandState.Confirmed,
            [(CommandState.Sent, CommandTrigger.Reject)] = CommandState.Rejected,
            [(CommandState.Sent, CommandTrigger.TimeOut)] = CommandState.Reconciling,
            [(CommandState.Reconciling, CommandTrigger.Confirm)] = CommandState.Confirmed,
            [(CommandState.Reconciling, CommandTrigger.Resend)] = CommandState.Published,
            [(CommandState.Reconciling, CommandTrigger.GiveUp)] = CommandState.DeadLettered,
        }.ToFrozenDictionary();

    /// <summary>Returns whether an allowed trigger is the outcome of handing the command to a provider.</summary>
    /// <param name="from">The state the command is in.</param>
    /// <param name="trigger">The trigger being applied.</param>
    /// <returns><see langword="true"/> for every allowed trigger from <see cref="CommandState.Published"/>, the only state in which a send happens.</returns>
    public static bool CountsAsAttempt(CommandState from, CommandTrigger trigger) =>
        from == CommandState.Published && Table.ContainsKey((from, trigger));
}
