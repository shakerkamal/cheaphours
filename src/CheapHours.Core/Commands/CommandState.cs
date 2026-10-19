namespace CheapHours.Core.Commands;

/// <summary>
/// Where a command is in its lifecycle (D7, confirmed against the System Design diagram). Allowed
/// transitions are defined by the command state machine.
/// </summary>
public enum CommandState
{
    /// <summary>Saved with its outbox entry; not yet on the broker.</summary>
    Pending,

    /// <summary>Published to the broker by the outbox relay.</summary>
    Published,

    /// <summary>Handed to the provider, which accepted it; waiting for the device to confirm.</summary>
    Sent,

    /// <summary>The device reported the new state. Terminal.</summary>
    Confirmed,

    /// <summary>No confirmation in time; reading the device's state before deciding to resend.</summary>
    Reconciling,

    /// <summary>The provider or device refused it permanently. Terminal.</summary>
    Rejected,

    /// <summary>Retries exhausted; moved to the dead-letter queue and alerted. Terminal.</summary>
    DeadLettered,
}
