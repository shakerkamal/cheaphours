namespace CheapHours.Core.Commands;

/// <summary>
/// What to do with a command that timed out waiting for confirmation. Exactly three outcomes exist: the
/// private constructor stops any other subtype.
/// </summary>
public abstract record ReconcileDecision
{
    private ReconcileDecision()
    {
    }

    /// <summary>The device is in the requested state: apply <see cref="Command.Confirm"/>.</summary>
    public sealed record Confirm : ReconcileDecision;

    /// <summary>Not proven yet and attempts remain: apply <see cref="Command.Resend"/> (same idempotency key).</summary>
    public sealed record Resend : ReconcileDecision;

    /// <summary>The command cannot take effect: apply <see cref="Command.GiveUp"/> with <see cref="Reason"/>.</summary>
    public sealed record Fail : ReconcileDecision
    {
        /// <summary>Creates the decision.</summary>
        /// <param name="reason">Why the command is given up.</param>
        public Fail(string reason) => Reason = Guard.NotBlank(reason);

        /// <summary>Gets why the command is given up.</summary>
        public string Reason { get; }
    }
}
