using CheapHours.Core.Ids;
using Microsoft.Extensions.Options;

namespace CheapHours.Providers.Ocpp;

/// <summary>
/// Validates <see cref="OcppOptions"/> at startup. Messages name the entry (by index and id) but never
/// include a password hash.
/// </summary>
public sealed class OcppOptionsValidator : IValidateOptions<OcppOptions>
{
    /// <summary>The longest heartbeat interval accepted (one day).</summary>
    public const int MaxHeartbeatIntervalSeconds = 86_400;

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, OcppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.HeartbeatIntervalSeconds is <= 0 or > MaxHeartbeatIntervalSeconds)
        {
            errors.Add($"{OcppOptions.SectionName}:HeartbeatIntervalSeconds must be between 1 and {MaxHeartbeatIntervalSeconds}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.ChargePoints.Count; i++)
        {
            var cp = options.ChargePoints[i];
            if (!OcppChargePointId.IsValid(cp.Id))
            {
                errors.Add($"{OcppOptions.SectionName}:ChargePoints:{i}: Id must be 1-{OcppChargePointId.MaxLength} characters from [A-Za-z0-9._:-].");
                continue;
            }

            var entry = $"{OcppOptions.SectionName}:ChargePoints:{i} ({cp.Id})";
            if (!seen.Add(cp.Id))
            {
                errors.Add($"{entry}: the id is registered more than once.");
            }

            if (!OwnerId.TryParse(cp.OwnerId, out _))
            {
                errors.Add($"{entry}: OwnerId must be an owner id such as own_demo.");
            }

            if (!IsSha256Hex(cp.PasswordHash))
            {
                errors.Add($"{entry}: PasswordHash must be the SHA-256 of the password as 64 hex characters (set it with dotnet user-secrets).");
            }

            if (cp.MaxPowerW <= 0)
            {
                errors.Add($"{entry}: MaxPowerW must be greater than zero.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
