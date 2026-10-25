namespace CheapHours.Host.Gateway;

/// <summary>Why a charger connection ended; decides whether the device goes offline and the registry entry goes.</summary>
internal enum CloseReason
{
    /// <summary>Still open.</summary>
    None,

    /// <summary>The charger closed the socket or it dropped.</summary>
    ClientClosed,

    /// <summary>A newer connection for the same charger took over.</summary>
    Replaced,

    /// <summary>Nothing was heard for more than three heartbeat intervals.</summary>
    HeartbeatTimeout,

    /// <summary>The charger sent a binary or oversized message.</summary>
    ProtocolViolation,

    /// <summary>The gateway is shutting down; the charger is expected to reconnect elsewhere.</summary>
    Shutdown,

    /// <summary>The gateway could not set the connection up (for example the database was unreachable).</summary>
    ServerError,
}
