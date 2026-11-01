using System.Text.Json;
using CheapHours.Providers.Kit;

namespace CheapHours.Providers.CarCloud;

/// <summary>
/// One vehicle's state from <c>GET /api/v1/vehicles/{id}/state</c>:
/// <c>{ timestamp, online, pluggedIn, chargingState, socPercent, chargePowerKw?, sessionEnergyKwh }</c>.
/// </summary>
/// <param name="Timestamp">When the vendor took the state (UTC).</param>
/// <param name="Online">Whether the vehicle is reachable (not asleep).</param>
/// <param name="ChargingState"><c>Charging</c>, <c>Stopped</c>, <c>Complete</c> or <c>Unplugged</c>.</param>
/// <param name="SocPercent">The state of charge in percent, if given.</param>
/// <param name="ChargePowerKw">The charging power in kW, if given.</param>
/// <param name="SessionEnergyKwh">The energy added in the current session in kWh.</param>
internal sealed record CarCloudVehicleState(DateTimeOffset Timestamp, bool Online, string ChargingState, decimal? SocPercent, decimal? ChargePowerKw, decimal SessionEnergyKwh)
{
    /// <summary>Reads a state; any problem is described, never thrown.</summary>
    /// <param name="json">The body.</param>
    /// <param name="state">The state when usable.</param>
    /// <param name="problem">What is wrong with it otherwise (no values from the body).</param>
    /// <returns><see langword="true"/> when usable.</returns>
    public static bool TryParse(string json, out CarCloudVehicleState state, out string problem)
    {
        state = null!;
        problem = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "state is not an object";
                return false;
            }

            if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String || !VendorUnits.TryParseUtc(timestamp.GetString(), out var at))
            {
                problem = "state timestamp missing or without an offset";
                return false;
            }

            var online = !root.TryGetProperty("online", out var onlineValue) || onlineValue.ValueKind != JsonValueKind.False;
            var chargingState = root.TryGetProperty("chargingState", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString()! : "Unplugged";
            decimal? soc = root.TryGetProperty("socPercent", out var socValue) && socValue.TryGetDecimal(out var s) ? s : null;
            decimal? power = root.TryGetProperty("chargePowerKw", out var powerValue) && powerValue.TryGetDecimal(out var p) ? p : null;
            var energy = root.TryGetProperty("sessionEnergyKwh", out var energyValue) && energyValue.TryGetDecimal(out var e) ? e : (decimal?)null;

            if (online && energy is null)
            {
                problem = "session energy missing";
                return false;
            }

            if (soc is < 0 or > 100 || energy < 0)
            {
                problem = "state of charge or session energy out of range";
                return false;
            }

            state = new CarCloudVehicleState(at, online, chargingState, soc, power, energy ?? 0);
            return true;
        }
        catch (JsonException)
        {
            problem = "state is not JSON";
            return false;
        }
    }
}
