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

    private Command(DeviceCommand request)
    {
        Request = request;
        History = new ReadOnlyCollection<CommandHistoryEntry>(_history);
    }

    /// <summary>Gets what the device is asked to do.</summary>
    public DeviceCommand Request { get; }

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
    /// <returns>The new command.</returns>
    /// <exception cref="ArgumentException"><paramref name="at"/> is not UTC.</exception>
    public static Command Create(DeviceCommand request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new Command(request);
        command._history.Add(new CommandHistoryEntry(CommandState.Pending, Guard.Utc(at)));
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
