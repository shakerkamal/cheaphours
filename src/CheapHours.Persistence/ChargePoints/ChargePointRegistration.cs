using CheapHours.Core.Ids;

namespace CheapHours.Persistence.ChargePoints;

/// <summary>A registered charger: who owns it and the SHA-256 of its basic-auth password.</summary>
/// <param name="ChargePointId">The id in the charger's WebSocket URL.</param>
/// <param name="OwnerId">The owner its device belongs to.</param>
/// <param name="PasswordHash">SHA-256 of the password, as 64 lower-case hex characters. Never log it.</param>
/// <param name="MaxPowerW">The charger's maximum power (D2).</param>
/// <param name="CreatedAt">When it was first registered; set by the store.</param>
/// <param name="RevokedAt">When it was blocked, if it was.</param>
public sealed record ChargePointRegistration(
    string ChargePointId,
    OwnerId OwnerId,
    string PasswordHash,
    int MaxPowerW,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? RevokedAt = null)
{
    /// <summary>Gets whether the registration is blocked.</summary>
    public bool IsRevoked => RevokedAt is not null;

    /// <inheritdoc/>
    public override string ToString() => $"ChargePointRegistration {{ ChargePointId = {ChargePointId}, OwnerId = {OwnerId}, MaxPowerW = {MaxPowerW}, RevokedAt = {RevokedAt} }}";
}
