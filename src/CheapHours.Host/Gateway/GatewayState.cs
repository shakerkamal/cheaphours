namespace CheapHours.Host.Gateway;

/// <summary>Whether the gateway can take chargers: initialised (indexes and registrations in place) and not draining.</summary>
internal sealed class GatewayState
{
    private volatile bool _initialised;
    private volatile bool _draining;

    /// <summary>Gets whether the startup work (indexes, registration seeding) has finished.</summary>
    public bool IsInitialised => _initialised;

    /// <summary>Gets whether shutdown has begun.</summary>
    public bool IsDraining => _draining;

    /// <summary>Gets whether new charger connections are accepted.</summary>
    public bool AcceptsConnections => _initialised && !_draining;

    /// <summary>Marks the startup work as done.</summary>
    public void MarkInitialised() => _initialised = true;

    /// <summary>Marks the start of shutdown; from now on upgrades get 503 and readiness fails.</summary>
    public void BeginDraining() => _draining = true;
}
