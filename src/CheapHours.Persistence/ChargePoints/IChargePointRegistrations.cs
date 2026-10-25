namespace CheapHours.Persistence.ChargePoints;

/// <summary>The <c>chargePoints</c> collection: which chargers may connect, and with which password hash.</summary>
public interface IChargePointRegistrations
{
    /// <summary>Finds a registration by charge point id; one indexed read, no cache, so revocation is immediate.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The registration, or <see langword="null"/> when none exists.</returns>
    Task<ChargePointRegistration?> FindAsync(string chargePointId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the registration or updates its owner, hash and power. Never changes <c>createdAt</c> or
    /// <c>revokedAt</c> of an existing registration.
    /// </summary>
    /// <param name="registration">The configured registration.</param>
    /// <param name="now">The current time (UTC), stored as <c>createdAt</c> on insert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the registration is stored.</returns>
    Task SeedAsync(ChargePointRegistration registration, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Blocks a charger; the row is kept.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <param name="at">When it was revoked (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the registration is revoked.</returns>
    Task RevokeAsync(string chargePointId, DateTimeOffset at, CancellationToken cancellationToken);
}
