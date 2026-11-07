using System.Collections.ObjectModel;
using CheapHours.Core.Ids;

namespace CheapHours.Core.Commands;

/// <summary>
/// A device command on its way from creation to a terminal state. State changes only through the methods
/// below, each of which applies one trigger from <see cref="CommandTransitions.Table"/>; anything else throws
/// <see cref="InvalidCommandTransitionException"/> and leaves the command unchanged. Times are passed in by
/// the caller, who reads the clock once per message.
/// </summary>
public sealed class Command
{
    private readonly List<CommandHistoryEntry> _history = [];

    private Command(DeviceCommand request, DateTimeOffset dueAt)
    {
        Request = request;
        DueAt = Guard.Utc(dueAt);
        History = new ReadOnlyCollection<CommandHistoryEntry>(_history);
    }

    /// <summary>Gets what the device is asked to do.</summary>
    public DeviceCommand Request { get; }

    /// <summary>Gets the time (UTC) from which the command may be sent to the device.</summary>
    public DateTimeOffset DueAt { get; }

    /// <summary>Gets the command's id.</summary>
    public CommandId Id => Request.Id;

    /// <summary>Gets the key that makes repeated sends safe; it never changes, including on resend.</summary>
    public string IdempotencyKey => Request.IdempotencyKey;

    /// <summary>Gets the current state.</summary>
    public CommandState State { get; private set; }

    /// <summary>Gets how many times the command has been handed to a provider.</summary>
    public int Attempts { get; private set; }

    /// <summary>Gets when the next send attempt is due, after a transient failure; otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Gets every state the command entered, oldest first; times never decrease.</summary>
    public IReadOnlyList<CommandHistoryEntry> History { get; }

    /// <summary>Gets when the command last entered <see cref="CommandState.Sent"/>, if it has.</summary>
    public DateTimeOffset? LastSentAt
    {
        get
        {
            for (var i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].State == CommandState.Sent)
                {
                    return _history[i].At;
                }
            }

            return null;
        }
    }

    /// <summary>Creates a command in <see cref="CommandState.Pending"/> with zero attempts.</summary>
    /// <param name="request">What the device is asked to do.</param>
    /// <param name="at">The creation time (UTC).</param>
    /// <param name="dueAt">When the command may be sent (UTC); <paramref name="at"/> when omitted.</param>
    /// <returns>The new command.</returns>
    /// <exception cref="ArgumentException"><paramref name="at"/> or <paramref name="dueAt"/> is not UTC.</exception>
    public static Command Create(DeviceCommand request, DateTimeOffset at, DateTimeOffset? dueAt = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guard.Utc(at);
        var command = new Command(request, dueAt ?? at);
        command._history.Add(new CommandHistoryEntry(CommandState.Pending, at));
        return command;
    }

    /// <summary>
    /// Rebuilds a stored command by replaying its history through the transition table, so a stored command
    /// can only be one the table could have produced. State and attempts follow from the history.
    /// </summary>
    /// <param name="request">What the device is asked to do.</param>
    /// <param name="dueAt">When the command may be sent (UTC).</param>
    /// <param name="history">Every state the command entered, oldest first, starting with <see cref="CommandState.Pending"/>.</param>
    /// <param name="nextAttemptAt">The stored next attempt; set exactly when the last step was a retry.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentException">The history is empty, does not start pending, breaks a rule of the triggers, or disagrees with <paramref name="nextAttemptAt"/>.</exception>
    /// <exception cref="InvalidCommandTransitionException">Two consecutive entries are not a transition in the table.</exception>
    public static Command Restore(
        DeviceCommand request,
        DateTimeOffset dueAt,
        IReadOnlyList<CommandHistoryEntry> history,
        DateTimeOffset? nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[0].State != CommandState.Pending)
        {
            throw new ArgumentException("A command's history starts with Pending.", nameof(history));
        }

        var command = new Command(request, dueAt);
        command._history.Add(history[0]);
        for (var i = 1; i < history.Count; i++)
        {
            var entry = history[i];
            var trigger = TriggerFor(command.State, entry.State);
            var isLast = i == history.Count - 1;
            DateTimeOffset? next = null;
            if (trigger == CommandTrigger.RetryLater)
            {
                // Earlier retries have passed; only the last one's schedule is stored.
                next = isLast
                    ? nextAttemptAt ?? throw new ArgumentException("The last step is a retry, so the next attempt must be stored.", nameof(nextAttemptAt))
                    : entry.At;
                if (next < entry.At)
                {
                    throw new ArgumentOutOfRangeException(nameof(nextAttemptAt), next, $"The next attempt cannot be before {entry.At:O}.");
                }
            }

            if (trigger is CommandTrigger.Reject or CommandTrigger.RetryLater or CommandTrigger.GiveUp or CommandTrigger.Cancel)
            {
                Guard.NotBlank(entry.Reason ?? string.Empty, nameof(history));
            }

            command.Apply(trigger, entry.At, entry.VendorReference, entry.Reason, next);
        }

        if (nextAttemptAt is not null && command.NextAttemptAt is null)
        {
            throw new ArgumentException("A next attempt is stored, but the last step was not a retry.", nameof(nextAttemptAt));
        }

        return command;
    }

    /// <summary>Returns whether <paramref name="trigger"/> is allowed in the current state.</summary>
    /// <param name="trigger">The trigger to test.</param>
    /// <returns><see langword="true"/> if the transition table allows it; lets consumers drop duplicate or late events without catching.</returns>
    public bool CanFire(CommandTrigger trigger) => CommandTransitions.Table.ContainsKey((State, trigger));

    /// <summary>The outbox relay published the command: <see cref="CommandState.Pending"/> → <see cref="CommandState.Published"/>.</summary>
    /// <param name="at">When (UTC).</param>
    public void Publish(DateTimeOffset at) => Apply(CommandTrigger.Publish, at);

    /// <summary>The provider accepted the command: <see cref="CommandState.Published"/> → <see cref="CommandState.Sent"/>.</summary>
    /// <param name="at">When (UTC).</param>
    /// <param name="vendorReference">The vendor's reference for the request, if any.</param>
    public void Accept(DateTimeOffset at, string? vendorReference = null) =>
        Apply(CommandTrigger.Accept, at, vendorReference: vendorReference);

    /// <summary>The provider or device refused the command: <see cref="CommandState.Published"/> or <see cref="CommandState.Sent"/> → <see cref="CommandState.Rejected"/>.</summary>
    /// <param name="at">When (UTC).</param>
    /// <param name="reason">Why it was refused.</param>
    public void Reject(DateTimeOffset at, string reason) =>
        Apply(CommandTrigger.Reject, at, reason: Guard.NotBlank(reason));

    /// <summary>The send failed transiently and will be retried: stays <see cref="CommandState.Published"/>.</summary>
    /// <param name="at">When (UTC).</param>
    /// <param name="nextAttemptAt">When the next attempt is due (UTC, not before <paramref name="at"/>).</param>
    /// <param name="reason">What went wrong.</param>
    public void RetryLater(DateTimeOffset at, DateTimeOffset nextAttemptAt, string reason)
    {
        Guard.Utc(nextAttemptAt);
        if (nextAttemptAt < at)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAt), nextAttemptAt, $"The next attempt cannot be before {at:O}.");
        }

        Apply(CommandTrigger.RetryLater, at, reason: Guard.NotBlank(reason), nextAttemptAt: nextAttemptAt);
    }

    /// <summary>No attempts left, or the command cannot take effect: <see cref="CommandState.Published"/> or <see cref="CommandState.Reconciling"/> → <see cref="CommandState.DeadLettered"/>.</summary>
    /// <param name="at">When (UTC).</param>
    /// <param name="reason">Why the command was given up.</param>
    public void GiveUp(DateTimeOffset at, string reason) =>
        Apply(CommandTrigger.GiveUp, at, reason: Guard.NotBlank(reason));

    /// <summary>The device reported the requested state: <see cref="CommandState.Sent"/> or <see cref="CommandState.Reconciling"/> → <see cref="CommandState.Confirmed"/>.</summary>
    /// <param name="at">When (UTC).</param>
    public void Confirm(DateTimeOffset at) => Apply(CommandTrigger.Confirm, at);

    /// <summary>No confirmation within the timeout: <see cref="CommandState.Sent"/> → <see cref="CommandState.Reconciling"/>.</summary>
    /// <param name="at">When (UTC).</param>
    public void TimeOut(DateTimeOffset at) => Apply(CommandTrigger.TimeOut, at);

    /// <summary>Reconciling decided to send again with the same key: <see cref="CommandState.Reconciling"/> → <see cref="CommandState.Published"/>.</summary>
    /// <param name="at">When (UTC).</param>
    public void Resend(DateTimeOffset at) => Apply(CommandTrigger.Resend, at);

    /// <summary>The command is withdrawn before it was handed to the device: <see cref="CommandState.Pending"/> or <see cref="CommandState.Published"/> → <see cref="CommandState.Cancelled"/>. Not an attempt.</summary>
    /// <param name="at">When (UTC).</param>
    /// <param name="reason">Why, for example <c>Superseded by plan_8812:v4</c>.</param>
    public void Cancel(DateTimeOffset at, string reason) =>
        Apply(CommandTrigger.Cancel, at, reason: Guard.NotBlank(reason));

    // Each (from, to) pair in the table belongs to exactly one trigger, so a history names its triggers.
    private static CommandTrigger TriggerFor(CommandState from, CommandState to)
    {
        foreach (var ((state, trigger), next) in CommandTransitions.Table)
        {
            if (state == from && next == to)
            {
                return trigger;
            }
        }

        throw new InvalidCommandTransitionException(from, to);
    }

    // The one place state changes: every check runs before anything is mutated.
    private void Apply(
        CommandTrigger trigger,
        DateTimeOffset at,
        string? vendorReference = null,
        string? reason = null,
        DateTimeOffset? nextAttemptAt = null)
    {
        if (!CommandTransitions.Table.TryGetValue((State, trigger), out var next))
        {
            throw new InvalidCommandTransitionException(State, trigger);
        }

        Guard.Utc(at);
        var last = _history[^1].At;
        if (at < last)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, $"Cannot be earlier than the last history entry ({last:O}).");
        }

        if (CommandTransitions.CountsAsAttempt(State, trigger))
        {
            Attempts++;
        }

        State = next;
        NextAttemptAt = nextAttemptAt;
        _history.Add(new CommandHistoryEntry(next, at, vendorReference, reason));
    }
}
