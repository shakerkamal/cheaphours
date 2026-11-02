using System.Globalization;
using System.Text.Json;

namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>
/// The simulated cloud's state, in memory under one lock: heat pumps whose lifetime energy grows with their power
/// over time from the injected clock, each heat pump's last event, and a log of the last 200 delivery attempts.
/// Event bodies are serialised once, so every attempt and every redelivery sends the same bytes.
/// </summary>
internal sealed class SimulatorState
{
    /// <summary>The operating modes the vendor knows.</summary>
    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { "Heating", "HotWater", "Defrost", "Standby", "Fault" };

    private const int LogSize = 200;

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly string _accountId;
    private readonly Dictionary<string, HeatPump> _heatPumps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebhookEvent> _lastEvents = new(StringComparer.Ordinal);
    private readonly LinkedList<Delivery> _deliveries = new();

    public SimulatorState(SimulatorOptions options, TimeProvider time)
    {
        _time = time;
        _accountId = options.AccountId;
        var now = time.GetUtcNow();
        foreach (var o in options.HeatPumps)
        {
            _heatPumps[o.Id] = new HeatPump(o.Id, o.Maker, o.Model, o.RatedKw, o.StandbyKw, o.Mode, o.EnergyKwh, now);
        }
    }

    /// <summary>Creates a <c>device.state</c> event for every heat pump still reported.</summary>
    public IReadOnlyList<WebhookEvent> StateEvents()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var events = new List<WebhookEvent>();
            foreach (var heatPump in _heatPumps.Values.Where(h => !h.Removed))
            {
                var eventId = NewEventId();
                var body = new
                {
                    eventId,
                    type = "device.state",
                    occurredAt = Timestamp(now),
                    accountId = _accountId,
                    device = new { id = heatPump.Id, maker = heatPump.Maker, model = heatPump.Model },
                    state = new { online = true, mode = heatPump.Mode, powerKw = heatPump.PowerKw, energyKwh = decimal.Round(heatPump.EnergyAt(now), 4) },
                };
                events.Add(Remember(heatPump.Id, eventId, body.type, body));
            }

            return events;
        }
    }

    /// <summary>Returns a heat pump's last event, unchanged, or <see langword="null"/> when it has none.</summary>
    public WebhookEvent? LastEvent(string heatPumpId)
    {
        lock (_lock)
        {
            return _lastEvents.GetValueOrDefault(heatPumpId);
        }
    }

    /// <summary>Changes a heat pump's mode; the energy used so far is kept.</summary>
    /// <returns><see langword="null"/> when the heat pump is unknown, <see langword="false"/> when the mode is.</returns>
    public bool? SetMode(string heatPumpId, string mode)
    {
        lock (_lock)
        {
            if (!_heatPumps.TryGetValue(heatPumpId, out var heatPump) || heatPump.Removed)
            {
                return null;
            }

            if (!Modes.Contains(mode))
            {
                return false;
            }

            heatPump.ChangeMode(mode, _time.GetUtcNow());
            return true;
        }
    }

    /// <summary>Removes a heat pump: it is no longer reported, and a <c>device.deleted</c> event is returned to deliver.</summary>
    public WebhookEvent? Remove(string heatPumpId)
    {
        lock (_lock)
        {
            if (!_heatPumps.TryGetValue(heatPumpId, out var heatPump) || heatPump.Removed)
            {
                return null;
            }

            heatPump.Removed = true;
            var eventId = NewEventId();
            var body = new { eventId, type = "device.deleted", occurredAt = Timestamp(_time.GetUtcNow()), accountId = _accountId, device = new { id = heatPump.Id } };
            return Remember(heatPump.Id, eventId, body.type, body);
        }
    }

    /// <summary>Logs one delivery attempt.</summary>
    public void Log(WebhookEvent webhookEvent, int attempt, int? statusCode, string? error)
    {
        lock (_lock)
        {
            _deliveries.AddLast(new Delivery(webhookEvent.EventId, webhookEvent.HeatPumpId, webhookEvent.Type, attempt, statusCode, error, _time.GetUtcNow()));
            while (_deliveries.Count > LogSize)
            {
                _deliveries.RemoveFirst();
            }
        }
    }

    /// <summary>Returns the logged attempts, oldest first.</summary>
    public IReadOnlyList<Delivery> Deliveries()
    {
        lock (_lock)
        {
            return [.. _deliveries];
        }
    }

    private static string NewEventId() => "evt_" + Guid.NewGuid().ToString("N");

    private static string Timestamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private WebhookEvent Remember<T>(string heatPumpId, string eventId, string type, T body)
    {
        var webhookEvent = new WebhookEvent(eventId, heatPumpId, type, JsonSerializer.SerializeToUtf8Bytes(body));
        _lastEvents[heatPumpId] = webhookEvent;
        return webhookEvent;
    }

    /// <summary>One logged delivery attempt.</summary>
    public sealed record Delivery(string EventId, string HeatPumpId, string Type, int Attempt, int? StatusCode, string? Error, DateTimeOffset At);

    private sealed class HeatPump(string id, string maker, string model, decimal ratedKw, decimal standbyKw, string mode, decimal energyKwh, DateTimeOffset since)
    {
        private decimal _energyAtChange = energyKwh;
        private DateTimeOffset _changedAt = since;

        public string Id { get; } = id;

        public string Maker { get; } = maker;

        public string Model { get; } = model;

        public string Mode { get; private set; } = mode;

        public bool Removed { get; set; }

        public decimal PowerKw => Mode switch
        {
            "Heating" or "HotWater" or "Defrost" => ratedKw,
            "Standby" => standbyKw,
            _ => 0m, // Fault
        };

        public decimal EnergyAt(DateTimeOffset now) =>
            _energyAtChange + (PowerKw * (now - _changedAt).Ticks / TimeSpan.TicksPerHour);

        public void ChangeMode(string newMode, DateTimeOffset now)
        {
            _energyAtChange = EnergyAt(now);
            _changedAt = now;
            Mode = newMode;
        }
    }
}

/// <summary>One webhook, serialised once: every attempt and every redelivery sends <see cref="Body"/> unchanged.</summary>
/// <param name="EventId">The vendor's event id.</param>
/// <param name="HeatPumpId">The heat pump it is about.</param>
/// <param name="Type">The event type.</param>
/// <param name="Body">The raw JSON body.</param>
internal sealed record WebhookEvent(string EventId, string HeatPumpId, string Type, byte[] Body);
