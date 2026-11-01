using System.Text.Json;

namespace CheapHours.Providers.CarCloud;

/// <summary>One vehicle from <c>GET /api/v1/vehicles</c>.</summary>
/// <param name="Id">The vendor's vehicle id.</param>
/// <param name="Make">The maker, if given.</param>
/// <param name="Model">The model, if given.</param>
/// <param name="BatteryCapacityKwh">The usable battery capacity in kWh, if given.</param>
internal sealed record CarCloudVehicle(string Id, string? Make, string? Model, decimal? BatteryCapacityKwh)
{
    /// <summary>Reads <c>{ "vehicles": [ { id, make, model, batteryCapacityKwh } ] }</c>; entries without an id are left out.</summary>
    /// <param name="json">The body.</param>
    /// <param name="vehicles">The vehicles when readable.</param>
    /// <returns><see langword="true"/> when the body had a vehicle list.</returns>
    public static bool TryParseList(string json, out IReadOnlyList<CarCloudVehicle> vehicles)
    {
        vehicles = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("vehicles", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            vehicles =
            [
                .. list.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 })
                    .Select(v => new CarCloudVehicle(
                        v.GetProperty("id").GetString()!,
                        Text(v, "make"),
                        Text(v, "model"),
                        v.TryGetProperty("batteryCapacityKwh", out var capacity) && capacity.TryGetDecimal(out var kwh) ? kwh : null)),
            ];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
