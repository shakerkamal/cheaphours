using System.Globalization;
using System.Text.Json;
using CheapHours.Providers.Kit.Testing;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>
/// The car maker's cloud on WireMock (the API in the provider's README): an OAuth token endpoint, the vehicle
/// list, vehicle states and idempotent start/stop. Tests arrange vehicles and their states directly.
/// </summary>
internal sealed class CarCloudVendorFake : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Vehicle> _vehicles = new(StringComparer.Ordinal);
    private int _requestIds;

    public CarCloudVendorFake()
    {
        Server = new WireMockVendorFake(oauth: true);
        Server.Map(HttpMethod.Get, "^/api/v1/vehicles$", (_, _) => List());
        Server.Map(HttpMethod.Get, "^/api/v1/vehicles/(?<id>[^/]+)/state$", (_, m) => State(Uri.UnescapeDataString(m.Groups["id"].Value)));
        Server.Map(HttpMethod.Post, "^/api/v1/vehicles/(?<id>[^/]+)/charging/(?<action>start|stop)$", (r, m) => Command(Uri.UnescapeDataString(m.Groups["id"].Value), m.Groups["action"].Value, r.Header("Idempotency-Key")));
    }

    public WireMockVendorFake Server { get; }

    /// <summary>Adds a vehicle, plugged in and stopped, with a valid state.</summary>
    public void AddVehicle(string id, string make = "Voltwerk", string model = "Model E", decimal capacityKwh = 77m)
    {
        lock (_lock)
        {
            _vehicles[id] = new Vehicle(id, make, model, capacityKwh)
            {
                StateJson = StateJson("2026-10-28T18:00:00Z", online: true, chargingState: "Stopped", soc: 40m, powerKw: null, sessionKwh: 0m),
            };
        }
    }

    /// <summary>Sets a vehicle's state as the vendor would report it.</summary>
    public void SetState(string id, string timestamp, bool online = true, string chargingState = "Charging", decimal? soc = 42.5m, decimal? powerKw = 11m, decimal sessionKwh = 3.25m) =>
        Update(id, v => v.StateJson = StateJson(timestamp, online, chargingState, soc, powerKw, sessionKwh));

    /// <summary>Sets a vehicle's raw state body.</summary>
    public void SetStateJson(string id, string json) => Update(id, v => v.StateJson = json);

    /// <summary>Keeps the vehicle listed but makes its state answer 404, as if it was deleted between list and read.</summary>
    public void Remove(string id) => Update(id, v => v.StateStatus = 404);

    /// <summary>Makes the vehicle's state answer <paramref name="status"/>.</summary>
    public void FailState(string id, int status) => Update(id, v => v.StateStatus = status);

    public void Unplug(string id) => Update(id, v => v.PluggedIn = false);

    /// <summary>Gets how many sessions the vendor started for a vehicle (repeated keys do not count).</summary>
    public int StartsActed(string id)
    {
        lock (_lock)
        {
            return _vehicles[id].Starts;
        }
    }

    public void Dispose() => Server.Dispose();

    private static string StateJson(string timestamp, bool online, string chargingState, decimal? soc, decimal? powerKw, decimal sessionKwh)
    {
        var state = new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp,
            ["online"] = online,
            ["pluggedIn"] = chargingState != "Unplugged",
            ["chargingState"] = chargingState,
            ["socPercent"] = soc,
            ["sessionEnergyKwh"] = sessionKwh,
        };
        if (powerKw is not null)
        {
            state["chargePowerKw"] = powerKw;
        }

        return JsonSerializer.Serialize(state);
    }

    private void Update(string id, Action<Vehicle> change)
    {
        lock (_lock)
        {
            change(_vehicles[id]);
        }
    }

    private VendorAnswer List()
    {
        lock (_lock)
        {
            return VendorAnswer.Json(new
            {
                vehicles = _vehicles.Values.Select(v => new { id = v.Id, make = v.Make, model = v.Model, batteryCapacityKwh = v.CapacityKwh }),
            });
        }
    }

    private VendorAnswer State(string id)
    {
        lock (_lock)
        {
            if (!_vehicles.TryGetValue(id, out var vehicle))
            {
                return VendorAnswer.Raw(404, """{"error":"unknown_vehicle"}""");
            }

            return vehicle.StateStatus is { } status
                ? VendorAnswer.Raw(status, """{"error":"state_unavailable"}""")
                : VendorAnswer.Raw(200, vehicle.StateJson);
        }
    }

    private VendorAnswer Command(string id, string action, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return VendorAnswer.Raw(400, """{"error":"missing_idempotency_key"}""");
        }

        lock (_lock)
        {
            if (!_vehicles.TryGetValue(id, out var vehicle) || vehicle.StateStatus == 404)
            {
                return VendorAnswer.Raw(404, """{"error":"unknown_vehicle"}""");
            }

            if (vehicle.RequestIds.TryGetValue(key, out var earlier))
            {
                return VendorAnswer.Json(new { requestId = earlier }, 202);
            }

            if (action == "start" && !vehicle.PluggedIn)
            {
                return VendorAnswer.Raw(422, """{"error":"not_plugged_in"}""");
            }

            var requestId = "req-" + (++_requestIds).ToString(CultureInfo.InvariantCulture);
            vehicle.RequestIds[key] = requestId;
            if (action == "start")
            {
                vehicle.Starts++;
            }

            return VendorAnswer.Json(new { requestId }, 202);
        }
    }

    private sealed class Vehicle(string id, string make, string model, decimal capacityKwh)
    {
        public string Id { get; } = id;

        public string Make { get; } = make;

        public string Model { get; } = model;

        public decimal CapacityKwh { get; } = capacityKwh;

        public string StateJson { get; set; } = "{}";

        public int? StateStatus { get; set; }

        public bool PluggedIn { get; set; } = true;

        public int Starts { get; set; }

        public Dictionary<string, string> RequestIds { get; } = new(StringComparer.Ordinal);
    }
}
