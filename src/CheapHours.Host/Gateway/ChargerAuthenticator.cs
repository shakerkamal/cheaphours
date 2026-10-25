using System.Security.Cryptography;
using System.Text;
using CheapHours.Persistence.ChargePoints;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Checks a charger's basic-auth credentials against its registration (OCPP 1.6 security profile 1): the
/// username must be the charge point id in the URL and the password must hash to the stored SHA-256.
/// </summary>
/// <param name="registrations">The registrations store.</param>
internal sealed class ChargerAuthenticator(IChargePointRegistrations registrations)
{
    /// <summary>The failure category for an unregistered charger.</summary>
    public const string Unknown = "unknown";

    /// <summary>The failure category for a username that is not the path's charge point id.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>The failure category for a wrong password.</summary>
    public const string WrongPassword = "wrong-password";

    /// <summary>The failure category for a blocked registration.</summary>
    public const string Revoked = "revoked";

    // Compared against when there is no registration, so an unknown id costs the same as a wrong password.
    private static readonly byte[] DummyHash = SHA256.HashData("no registration"u8);

    /// <summary>Checks the credentials.</summary>
    /// <param name="chargePointId">The (already validated) charge point id from the path.</param>
    /// <param name="authorization">The <c>Authorization</c> header, if any.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The registration, or the failure category.</returns>
    public async Task<ChargerAuthResult> AuthenticateAsync(string chargePointId, string? authorization, CancellationToken cancellationToken)
    {
        if (!BasicCredentials.TryParse(authorization, out var username, out var password, out var failure))
        {
            return ChargerAuthResult.Failure(failure);
        }

        // Look up and hash on every path, so the response time does not tell which check failed.
        var registration = await registrations.FindAsync(chargePointId, cancellationToken).ConfigureAwait(false);
        var matches = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(password)),
            StoredHash(registration));

        if (!string.Equals(username, chargePointId, StringComparison.Ordinal))
        {
            return ChargerAuthResult.Failure(Mismatch);
        }

        if (registration is null)
        {
            return ChargerAuthResult.Failure(Unknown);
        }

        if (!matches)
        {
            return ChargerAuthResult.Failure(WrongPassword);
        }

        return registration.IsRevoked ? ChargerAuthResult.Failure(Revoked) : ChargerAuthResult.Success(registration);
    }

    private static byte[] StoredHash(ChargePointRegistration? registration)
    {
        if (registration is null)
        {
            return DummyHash;
        }

        try
        {
            return Convert.FromHexString(registration.PasswordHash);
        }
        catch (FormatException)
        {
            // A corrupt stored hash matches nothing.
            return DummyHash;
        }
    }
}
