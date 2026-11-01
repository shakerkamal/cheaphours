using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CheapHours.Simulators.CarCloud;

/// <summary>
/// The simulated cloud's state, in memory under one lock: accounts with their tokens and rate-limit window,
/// vehicles with their charging session, and remembered command responses. A restart returns to the
/// configured seed. Charge is computed on read from the clock: energy = power × elapsed, capped at a full battery.
/// </summary>
internal sealed class SimulatorState
{
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private readonly SimulatorOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Account> _byRefreshToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Account Account, DateTimeOffset ExpiresAt)> _byAccessToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vehicle> _vehicles = new(StringComparer.Ordinal);
    private int _requestIds;

    public SimulatorState(SimulatorOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        foreach (var configured in options.Accounts)
        {
            var account = new Account(configured.Id);
            _accounts[account.Id] = account;
            _byRefreshToken[configured.RefreshToken!] = account;
            var vehicles = configured.Vehicles.Count > 0 ? configured.Vehicles : [new SimulatorOptions.VehicleOptions()];
            foreach (var v in vehicles)
            {
                _vehicles[v.Id] = new Vehicle(v, account);
            }
        }
    }

    /// <summary>Refreshes with the refresh-token grant: rotates the refresh token and issues an access token.</summary>
    public TokenResult Refresh(string? clientId, string? clientSecret, string? grantType, string? refreshToken)
    {
        if (clientId != _options.ClientId || !FixedEquals(clientSecret, _options.ClientSecret))
        {
            return new TokenResult(401, "invalid_client", null, null);
        }

        lock (_lock)
        {
            if (grantType != "refresh_token" || refreshToken is null || !_byRefreshToken.Remove(refreshToken, out var account))
            {
                return new TokenResult(400, "invalid_grant", null, null);
            }

            var access = "sim-at-" + RandomNumberGenerator.GetHexString(24, lowercase: true);
            var refresh = "sim-rt-" + RandomNumberGenerator.GetHexString(24, lowercase: true);
            _byAccessToken[access] = (account, _time.GetUtcNow() + _options.AccessTokenLifetime);
            _byRefreshToken[refresh] = account;
            return new TokenResult(200, null, access, refresh);
        }
    }

    /// <summary>Finds the account of a live access token.</summary>
    public string? Authenticate(string? accessToken)
    {
        lock (_lock)
        {
            return accessToken is not null && _byAccessToken.TryGetValue(accessToken, out var entry) && entry.ExpiresAt > _time.GetUtcNow()
                ? entry.Account.Id
                : null;
        }
    }

    /// <summary>Counts a call against the account's fixed one-minute window.</summary>
    /// <returns><see langword="null"/> when allowed; otherwise the seconds until the window ends (at least 1).</returns>
    public int? Throttle(string accountId)
    {
        lock (_lock)
        {
            var account = _accounts[accountId];
            var now = _time.GetUtcNow();
            if (now - account.WindowStart >= TimeSpan.FromMinutes(1))
            {
                account.WindowStart = now;
                account.Calls = 0;
            }

            if (account.Calls >= _options.RequestsPerMinute)
            {
                return Math.Max(1, (int)Math.Ceiling((account.WindowStart + TimeSpan.FromMinutes(1) - now).TotalSeconds));
            }

            account.Calls++;
            return null;
        }
    }

    public IReadOnlyList<object> Vehicles(string accountId)
    {
        lock (_lock)
        {
            return
            [
                .. _vehicles.Values.Where(v => v.Account.Id == accountId).Select(v => new
                {
                    id = v.Options.Id,
                    make = v.Options.Make,
                    model = v.Options.Model,
                    batteryCapacityKwh = v.Options.BatteryCapacityKwh,
                }),
            ];
        }
    }

    /// <summary>Returns a vehicle's state now, or <see langword="null"/> when the account has no such vehicle.</summary>
    public Dictionary<string, object?>? State(string accountId, string vehicleId)
    {
        lock (_lock)
        {
            return Find(accountId, vehicleId) is { } vehicle ? vehicle.Snapshot(_time.GetUtcNow()) : null;
        }
    }

    /// <summary>Starts or stops charging, idempotent per (vehicle, key) for 24 hours.</summary>
    public CommandResult Command(string accountId, string vehicleId, string action, string key)
    {
        lock (_lock)
        {
            var vehicle = Find(accountId, vehicleId);
            if (vehicle is null)
            {
                return new CommandResult(404, new Dictionary<string, object?> { ["error"] = "unknown_vehicle" });
            }

            var now = _time.GetUtcNow();
            foreach (var expired in vehicle.Responses.Where(r => now - r.Value.At > IdempotencyWindow).Select(r => r.Key).ToList())
            {
                vehicle.Responses.Remove(expired);
            }

            if (vehicle.Responses.TryGetValue(key, out var remembered))
            {
                return remembered.Result;
            }

            CommandResult result;
            if (action == "start" && !vehicle.PluggedIn)
            {
                result = new CommandResult(422, new Dictionary<string, object?> { ["error"] = "not_plugged_in" });
            }
            else
            {
                if (action == "start")
                {
                    vehicle.Start(now);
                }
                else
                {
                    vehicle.Stop(now);
                }

                result = new CommandResult(202, new Dictionary<string, object?> { ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"sim-req-{++_requestIds}") });
            }

            vehicle.Responses[key] = (result, now);
            return result;
        }
    }

    public bool PlugIn(string vehicleId)
    {
        lock (_lock)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var vehicle))
            {
                return false;
            }

            vehicle.PluggedIn = true;
            return true;
        }
    }

    /// <summary>Starts a session the way the car's own app would (admin, no token).</summary>
    /// <returns><see langword="null"/> for an unknown vehicle, <see langword="false"/> when it is unplugged, otherwise <see langword="true"/>.</returns>
    public bool? StartFromCar(string vehicleId)
    {
        lock (_lock)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var vehicle))
            {
                return null;
            }

            if (!vehicle.PluggedIn)
            {
                return false;
            }

            vehicle.Start(_time.GetUtcNow());
            return true;
        }
    }

    public bool Unplug(string vehicleId)
    {
        lock (_lock)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var vehicle))
            {
                return false;
            }

            vehicle.Stop(_time.GetUtcNow());
            vehicle.PluggedIn = false;
            return true;
        }
    }

    /// <summary>Revokes every token of the account: refreshes answer <c>invalid_grant</c> and calls answer 401.</summary>
    public bool Revoke(string accountId)
    {
        lock (_lock)
        {
            if (!_accounts.ContainsKey(accountId))
            {
                return false;
            }

            foreach (var token in _byRefreshToken.Where(t => t.Value.Id == accountId).Select(t => t.Key).ToList())
            {
                _byRefreshToken.Remove(token);
            }

            foreach (var token in _byAccessToken.Where(t => t.Value.Account.Id == accountId).Select(t => t.Key).ToList())
            {
                _byAccessToken.Remove(token);
            }

            return true;
        }
    }

    private static bool FixedEquals(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private Vehicle? Find(string accountId, string vehicleId) =>
        _vehicles.TryGetValue(vehicleId, out var vehicle) && vehicle.Account.Id == accountId ? vehicle : null;

    public sealed record TokenResult(int Status, string? Error, string? AccessToken, string? RefreshToken)
    {
        public override string ToString() => $"TokenResult {{ Status = {Status}, Error = {Error} }}";
    }

    public sealed record CommandResult(int Status, Dictionary<string, object?> Body);

    private sealed class Account(string id)
    {
        public string Id { get; } = id;

        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.MinValue;

        public int Calls { get; set; }
    }

    private sealed class Vehicle(SimulatorOptions.VehicleOptions options, Account account)
    {
        private decimal _socAtStart = options.SocPercent;
        private DateTimeOffset? _sessionStart;
        private decimal _sessionEnergyKwh;

        public SimulatorOptions.VehicleOptions Options { get; } = options;

        public Account Account { get; } = account;

        public bool PluggedIn { get; set; } = true;

        public Dictionary<string, (CommandResult Result, DateTimeOffset At)> Responses { get; } = new(StringComparer.Ordinal);

        public void Start(DateTimeOffset now)
        {
            if (_sessionStart is not null)
            {
                return; // already charging: accepted without effect
            }

            _sessionStart = now;
            _sessionEnergyKwh = 0;
        }

        public void Stop(DateTimeOffset now)
        {
            if (_sessionStart is null)
            {
                return; // not charging: accepted without effect
            }

            var (energy, soc, _) = Charge(now);
            _sessionEnergyKwh = energy;
            _socAtStart = soc;
            _sessionStart = null;
        }

        public Dictionary<string, object?> Snapshot(DateTimeOffset now)
        {
            var (energy, soc, full) = Charge(now);
            var charging = _sessionStart is not null && !full;
            var state = new Dictionary<string, object?>
            {
                ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["online"] = true,
                ["pluggedIn"] = PluggedIn,
                ["chargingState"] = !PluggedIn ? "Unplugged" : charging ? "Charging" : full ? "Complete" : "Stopped",
                ["socPercent"] = decimal.Round(soc, 2),
                ["sessionEnergyKwh"] = decimal.Round(energy, 4),
            };
            if (charging)
            {
                state["chargePowerKw"] = Options.MaxChargePowerKw;
            }

            return state;
        }

        /// <summary>The session energy, the state of charge and whether the battery is full at <paramref name="now"/>.</summary>
        private (decimal EnergyKwh, decimal SocPercent, bool Full) Charge(DateTimeOffset now)
        {
            var capacity = Options.BatteryCapacityKwh;
            if (_sessionStart is not { } start)
            {
                return (_sessionEnergyKwh, _socAtStart, _socAtStart >= 100m);
            }

            var room = capacity * (100m - _socAtStart) / 100m;
            var energy = Options.MaxChargePowerKw * (decimal)(now - start).TotalHours;
            if (energy >= room)
            {
                return (room, 100m, true);
            }

            return (energy, _socAtStart + (energy / capacity * 100m), false);
        }
    }
}
