using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Options;

namespace CheapHours.Providers.HeatPumpCloud;

/// <summary>
/// Connects heat pumps through their maker's cloud, which posts signed webhooks (design §5). <see cref="Verify"/>
/// checks the <c>HeatPump-Signature</c> header, an HMAC-SHA256 over the timestamp and the raw body, and
/// <see cref="Translate"/> turns a verified event into normalised device events. It neither polls nor sends
/// commands, and does no I/O.
/// </summary>
public sealed class HeatPumpCloudProvider : IInboundTranslator, IWebhookVerifier
{
    /// <summary>The provider id.</summary>
    public const string Id = "heatpumpcloud";

    /// <summary>The header carrying the signature: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 over "&lt;t&gt;.&lt;body&gt;"&gt;</c>.</summary>
    public const string SignatureHeader = "HeatPump-Signature";

    /// <summary>The header carrying the vendor's event id.</summary>
    public const string EventIdHeader = "HeatPump-Event-Id";

    private const int SignatureBytes = 32;

    private readonly byte[]? _secret;
    private readonly TimeSpan _tolerance;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The validated options; the secret is read once, here.</param>
    public HeatPumpCloudProvider(IOptions<HeatPumpCloudOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        _secret = string.IsNullOrEmpty(value.SigningSecret) ? null : Encoding.UTF8.GetBytes(value.SigningSecret);
        _tolerance = value.SignatureTolerance;
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.PushesEvents;

    /// <inheritdoc/>
    public bool Verify(InboundMessage message)
    {
        try
        {
            return message is not null
                && _secret is not null
                && message.Headers.TryGetValue(SignatureHeader, out var header)
                && TryParseHeader(header, out var timestamp, out var signatures)
                && Matches(timestamp, message.Body.Span, signatures)
                && IsFresh(timestamp, message.ReceivedAt);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or CryptographicException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<DeviceEvent> Translate(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            using var document = JsonDocument.Parse(message.Body);
            return Translate(document.RootElement, message.ReceivedAt);
        }
        catch (JsonException)
        {
            return Malformed(message.ReceivedAt, "body is not JSON");
        }
        catch (OverflowException)
        {
            return Malformed(message.ReceivedAt, "power or energy out of range");
        }
    }

    private static List<DeviceEvent> Translate(JsonElement root, DateTimeOffset receivedAt)
    {
        if (root.ValueKind != JsonValueKind.Object || String(root, "type") is not { } type)
        {
            return Malformed(receivedAt, "event has no type");
        }

        if (type is not ("device.state" or "device.deleted"))
        {
            return []; // account.updated and the like: nothing about a device
        }

        if (!VendorUnits.TryParseUtc(String(root, "occurredAt"), out var at))
        {
            return Malformed(receivedAt, "occurredAt missing or without an offset");
        }

        if (!root.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object
            || String(device, "id") is not { } externalId || string.IsNullOrWhiteSpace(externalId))
        {
            return Malformed(receivedAt, "device id missing");
        }

        if (type == "device.deleted")
        {
            return [new DeviceEvent.DeviceRemoved(externalId, at)];
        }

        if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
        {
            return Malformed(receivedAt, "state missing");
        }

        var maker = String(device, "maker");
        var model = String(device, "model");
        var hardware = !string.IsNullOrWhiteSpace(maker) && !string.IsNullOrWhiteSpace(model) ? new DeviceHardware(maker, model) : null;
        var events = new List<DeviceEvent>
        {
            new DeviceEvent.Discovered(externalId, at, DeviceType.HeatPump, new DeviceAttributes()),
            new DeviceEvent.Connected(externalId, at, hardware),
        };

        if (state.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.False)
        {
            events.Add(new DeviceEvent.StatusChanged(externalId, at, DeviceStatus.Offline));
            return events;
        }

        if (String(state, "mode") is not { } mode
            || Decimal(state, "powerKw") is not { } powerKw
            || Decimal(state, "energyKwh") is not { } energyKwh)
        {
            return Malformed(receivedAt, "state needs mode, powerKw and energyKwh");
        }

        if (powerKw < 0 || energyKwh < 0)
        {
            return Malformed(receivedAt, "power or energy out of range");
        }

        events.Add(new DeviceEvent.StatusChanged(externalId, at, StatusOf(mode)));
        events.Add(new DeviceEvent.ReadingReported(new DeviceReading(
            externalId,
            at,
            VendorUnits.KilowattsToWatts(powerKw),
            VendorUnits.KilowattHoursToWattHours(energyKwh))));
        return events;
    }

    private static DeviceStatus StatusOf(string mode) => mode switch
    {
        "Heating" or "HotWater" or "Defrost" or "Standby" => DeviceStatus.Available,
        "Fault" => DeviceStatus.Faulted,
        _ => DeviceStatus.Unknown,
    };

    private static List<DeviceEvent> Malformed(DateTimeOffset receivedAt, string reason) => [new DeviceEvent.MalformedInput(receivedAt, reason)];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Decimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    /// <summary>Reads <c>t=&lt;digits&gt;</c> (exactly once) and every <c>v1=&lt;64 hex&gt;</c>; other keys are ignored.</summary>
    private static bool TryParseHeader(string header, out string timestamp, out List<byte[]> signatures)
    {
        timestamp = string.Empty;
        signatures = [];
        foreach (var part in header.Split(','))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key == "t")
            {
                if (timestamp.Length > 0 || value.Length is 0 or > 12 || !value.All(char.IsAsciiDigit))
                {
                    return false;
                }

                timestamp = value;
            }
            else if (key == "v1")
            {
                if (value.Length != SignatureBytes * 2 || !value.All(char.IsAsciiHexDigit))
                {
                    return false;
                }

                signatures.Add(Convert.FromHexString(value));
            }
        }

        return timestamp.Length > 0 && signatures.Count > 0;
    }

    private bool Matches(string timestamp, ReadOnlySpan<byte> body, List<byte[]> signatures)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(_secret!, payload);

        var match = false;
        foreach (var signature in signatures)
        {
            match |= CryptographicOperations.FixedTimeEquals(expected, signature); // no early exit
        }

        return match;
    }

    private bool IsFresh(string timestamp, DateTimeOffset receivedAt)
    {
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture));
        return (receivedAt - signedAt).Duration() <= _tolerance;
    }
}
