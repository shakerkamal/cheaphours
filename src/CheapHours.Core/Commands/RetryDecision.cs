namespace CheapHours.Core.Commands;

/// <summary>
/// What to do after a transient send failure. Exactly two outcomes exist: the private constructor stops any
/// other subtype.
/// </summary>
public abstract record RetryDecision
{
    private RetryDecision()
    {
    }

    /// <summary>Try again at <see cref="At"/>: apply <see cref="Command.RetryLater"/>.</summary>
    public sealed record RetryAt : RetryDecision
    {
        /// <summary>Creates the decision.</summary>
        /// <param name="at">When the next attempt is due (UTC).</param>
        public RetryAt(DateTimeOffset at) => At = Guard.Utc(at);

        /// <summary>Gets when the next attempt is due (UTC).</summary>
        public DateTimeOffset At { get; }
    }

    /// <summary>No attempts left: apply <see cref="Command.GiveUp"/>.</summary>
    public sealed record GiveUp : RetryDecision;
}
