using CheapHours.Persistence.ChargePoints;

namespace CheapHours.Host.Gateway;

/// <summary>The outcome of checking a charger's credentials.</summary>
/// <param name="Registration">The registration when the check passed.</param>
/// <param name="FailureReason">A category for logs when it failed: never the credentials themselves.</param>
internal sealed record ChargerAuthResult(ChargePointRegistration? Registration, string? FailureReason)
{
    /// <summary>Gets whether the charger may connect.</summary>
    public bool Succeeded => Registration is not null;

    /// <summary>A passed check.</summary>
    /// <param name="registration">The charger's registration.</param>
    /// <returns>The result.</returns>
    public static ChargerAuthResult Success(ChargePointRegistration registration) => new(registration, null);

    /// <summary>A failed check.</summary>
    /// <param name="reason">The failure category.</param>
    /// <returns>The result.</returns>
    public static ChargerAuthResult Failure(string reason) => new(null, reason);
}
