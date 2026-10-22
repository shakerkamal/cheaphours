namespace CheapHours.Core.Commands;

/// <summary>
/// Something that happened to a command. <see cref="CommandTransitions.Table"/> maps each allowed
/// (state, trigger) pair to the next state; every other pair is refused.
/// </summary>
public enum CommandTrigger
{
    /// <summary>The outbox relay put the command on the broker.</summary>
    Publish,

    /// <summary>The provider accepted the command.</summary>
    Accept,

    /// <summary>The provider or device refused the command permanently.</summary>
    Reject,

    /// <summary>The send failed transiently and another attempt is scheduled.</summary>
    RetryLater,

    /// <summary>No attempts are left, or reconciling showed the command cannot take effect.</summary>
    GiveUp,

    /// <summary>The device reported the requested state.</summary>
    Confirm,

    /// <summary>The device did not confirm within the confirmation timeout.</summary>
    TimeOut,

    /// <summary>Reconciling decided to send the command again with the same idempotency key.</summary>
    Resend,
}
