using CheapHours.Core.Commands;
using CheapHours.Persistence.Commands;

namespace CheapHours.Host.Commands;

/// <summary>
/// Applies the outcome of a send to a <c>Published</c> command through Core (command-dispatch design §5 step 4):
/// accepted → <c>Sent</c> (noting a device already in the requested state), rejected → <c>Rejected</c>, transient →
/// a retry no sooner than the vendor's <c>Retry-After</c>, or <c>DeadLettered</c> when no attempts are left. The claim
/// is cleared either way.
/// </summary>
internal sealed class CommandOutcomes(RetryPolicy retry)
{
    /// <summary>Applies <paramref name="result"/>; the caller has checked the command is <c>Published</c>.</summary>
    /// <param name="command">The command as read.</param>
    /// <param name="result">The send result.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The changed command, ready to save.</returns>
    public StoredCommand Apply(StoredCommand command, SendResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        var lifecycle = command.Command;
        var at = NotBefore(lifecycle, now);
        var alreadyInEffect = false;
        switch (result)
        {
            case SendResult.Accepted accepted:
                lifecycle.Accept(at, accepted.VendorReference);
                alreadyInEffect = accepted.VendorReference == CommandVendorReferences.AlreadyInEffect;
                break;
            case SendResult.Rejected rejected:
                lifecycle.Reject(at, rejected.Reason);
                break;
            case SendResult.Transient transient:
                switch (retry.AfterTransientFailure(lifecycle.Attempts + 1, transient.RetryAfter, at))
                {
                    case RetryDecision.RetryAt next:
                        lifecycle.RetryLater(at, next.At, transient.Reason);
                        break;
                    case RetryDecision.GiveUp:
                        lifecycle.GiveUp(at, transient.Reason);
                        break;
                }

                break;
        }

        return command with { Claim = null, AlreadyInEffect = alreadyInEffect };
    }

    /// <summary>Returns <paramref name="now"/>, or the command's last history time if the clock is behind it.</summary>
    /// <param name="command">The command.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>A time a trigger may use.</returns>
    public static DateTimeOffset NotBefore(Command command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var last = command.History[^1].At;
        return now > last ? now : last;
    }
}
