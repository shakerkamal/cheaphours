namespace CheapHours.Providers.Ocpp;

/// <summary>One charger registration in configuration (<c>Providers:Ocpp:ChargePoints:n</c>).</summary>
public sealed class ChargePointOptions
{
    /// <summary>Gets or sets the charge point id in the charger's WebSocket URL.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the owner id (<c>own_…</c>).</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 of the charger's password as 64 hex characters; never the password itself.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the charger's maximum power in W.</summary>
    public int MaxPowerW { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"ChargePointOptions {{ Id = {Id}, OwnerId = {OwnerId}, MaxPowerW = {MaxPowerW} }}";
}
