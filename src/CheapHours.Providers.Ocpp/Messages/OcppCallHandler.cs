using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// Turns one frame from a charger into an <see cref="OcppOutcome"/>: the reply and the normalised device
/// events. Pure: no I/O and no clock; the receipt time is passed in, and the caller records the events.
/// </summary>
public sealed class OcppCallHandler
{
    /// <summary>The OCPP-J timestamp format for times we send.</summary>
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The <see cref="DeviceEvent.MalformedInput"/> reason for a frame that is not OCPP-J.</summary>
    public const string MalformedFrameReason = "the frame is not OCPP-J";

    /// <summary>The <see cref="DeviceEvent.MalformedInput"/> reason for a payload that breaks its schema.</summary>
    public const string SchemaViolationReason = "the payload violates the OCPP 1.6 schema";

    /// <summary>The <see cref="DeviceEvent.MalformedInput"/> reason for meter values none of which is usable.</summary>
    public const string NoUsableMeterValueReason = "no usable meter value";

    private const string InternalErrorDescription = "The central system could not process the request.";

    // Transaction ids count seconds from here: positive, fits an int until 2094, and needs no stored state.
    private static readonly DateTimeOffset TransactionEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonObject AcceptedIdTag = new() { ["status"] = "Accepted" };

    private static readonly Dictionary<OcppErrorCode, string> Descriptions = new()
    {
        [OcppErrorCode.NotImplemented] = "The action is not known.",
        [OcppErrorCode.NotSupported] = "The action is not supported.",
        [OcppErrorCode.OccurenceConstraintViolation] = "A required field is missing.",
        [OcppErrorCode.TypeConstraintViolation] = "A field has the wrong type.",
        [OcppErrorCode.PropertyConstraintViolation] = "A field value is not allowed.",
    };

    private readonly OcppSchemaValidator _validator;
    private readonly int _intervalSeconds;

    /// <summary>Creates the handler.</summary>
    /// <param name="validator">The OCPP 1.6 schema validator.</param>
    /// <param name="heartbeatInterval">The interval sent in every <c>BootNotification</c> answer; whole seconds, greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">The interval is not a positive whole number of seconds.</exception>
    public OcppCallHandler(OcppSchemaValidator validator, TimeSpan heartbeatInterval)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (heartbeatInterval <= TimeSpan.Zero || heartbeatInterval.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), heartbeatInterval, "The heartbeat interval must be a positive whole number of seconds.");
        }

        _validator = validator;
        _intervalSeconds = (int)heartbeatInterval.TotalSeconds;
    }

    /// <summary>Handles one frame.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="chargePointId">The charger it came from; the external id of every event.</param>
    /// <param name="receivedAt">When the gateway received it (UTC); also the <c>currentTime</c> it reports.</param>
    /// <returns>The reply and the events.</returns>
    public OcppOutcome Handle(OcppFrame frame, string chargePointId, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(chargePointId);
        var at = receivedAt.ToUniversalTime();

        return frame switch
        {
            OcppCall call => HandleCall(call, chargePointId, at),
            OcppFrameError error => new OcppOutcome(
                OcppFrameCodec.WriteFormationViolation(error),
                [new DeviceEvent.MalformedInput(at, MalformedFrameReason)],
                OcppErrorCode.FormationViolation),

            // The gateway sends no calls yet, so any result or error from the charger is unsolicited.
            _ => OcppOutcome.None,
        };
    }

    /// <summary>The CallError sent when recording a valid call's events failed.</summary>
    /// <param name="uniqueId">The call's id.</param>
    /// <returns>An <c>InternalError</c> frame with no internal details.</returns>
    public static string InternalErrorReply(string uniqueId) =>
        OcppFrameCodec.WriteCallError(uniqueId, OcppErrorCode.InternalError, InternalErrorDescription);

    /// <summary>
    /// The id given to a transaction started at <paramref name="startedAt"/>: whole seconds since
    /// 2026-01-01Z, at least 1. Distinct for transactions a charger starts at least a second apart.
    /// </summary>
    /// <param name="startedAt">When the <c>StartTransaction</c> was received.</param>
    /// <returns>The transaction id.</returns>
    public static int TransactionIdAt(DateTimeOffset startedAt) =>
        (int)Math.Clamp(Math.Floor((startedAt - TransactionEpoch).TotalSeconds), 1, int.MaxValue);

    private static OcppOutcome Refuse(string uniqueId, OcppErrorCode code, IReadOnlyList<DeviceEvent>? events = null) =>
        new(OcppFrameCodec.WriteCallError(uniqueId, code, Descriptions[code]), events, code);

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) ? value.GetString() : null;

    private OcppOutcome HandleCall(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        if (!OcppActions.IsKnown(call.Action))
        {
            return Refuse(call.UniqueId, OcppErrorCode.NotImplemented);
        }

        if (call.Action is not (OcppActions.BootNotification or OcppActions.Heartbeat or OcppActions.StatusNotification
            or OcppActions.MeterValues or OcppActions.StartTransaction or OcppActions.StopTransaction))
        {
            return Refuse(call.UniqueId, OcppErrorCode.NotSupported);
        }

        if (_validator.ValidateRequest(call.Action, call.Payload) is { } violation)
        {
            return Refuse(call.UniqueId, violation, [new DeviceEvent.MalformedInput(receivedAt, SchemaViolationReason)]);
        }

        return call.Action switch
        {
            OcppActions.BootNotification => Boot(call, chargePointId, receivedAt),
            OcppActions.Heartbeat => new OcppOutcome(OcppFrameCodec.WriteCallResult(call.UniqueId, new JsonObject { ["currentTime"] = Time(receivedAt) })),
            OcppActions.StatusNotification => Status(call, chargePointId, receivedAt),
            OcppActions.MeterValues => Meter(call, chargePointId, receivedAt),
            OcppActions.StartTransaction => Start(call, chargePointId, receivedAt),
            _ => Stop(call, chargePointId, receivedAt),
        };
    }

    private static OcppOutcome Meter(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        var reply = OcppFrameCodec.WriteCallResult(call.UniqueId, []);
        if (!AppliesToDevice(call.Payload))
        {
            return new OcppOutcome(reply);
        }

        var readings = OcppMeterValues.Read(chargePointId, call.Payload.GetProperty("meterValue"), receivedAt);
        return readings.Count == 0
            ? new OcppOutcome(reply, [new DeviceEvent.MalformedInput(receivedAt, NoUsableMeterValueReason)])
            : new OcppOutcome(reply, Reported(readings));
    }

    private static OcppOutcome Start(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        var reply = new JsonObject
        {
            ["idTagInfo"] = AcceptedIdTag.DeepClone(),
            ["transactionId"] = TransactionIdAt(receivedAt),
        };

        var readings = new List<DeviceReading>();
        if (AppliesToDevice(call.Payload)
            && call.Payload.GetProperty("meterStart").TryGetInt64(out var meterStart)
            && OcppMeterValues.EnergyOnly(chargePointId, meterStart, OptionalString(call.Payload, "timestamp"), receivedAt) is { } start)
        {
            readings.Add(start);
        }

        return new OcppOutcome(OcppFrameCodec.WriteCallResult(call.UniqueId, reply), Reported(readings));
    }

    // A stop names no connector: its readings belong to the device.
    private static OcppOutcome Stop(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        var readings = new List<DeviceReading>();
        if (call.Payload.TryGetProperty("transactionData", out var data))
        {
            readings.AddRange(OcppMeterValues.Read(chargePointId, data, receivedAt));
        }

        if (call.Payload.GetProperty("meterStop").TryGetInt64(out var meterStop)
            && OcppMeterValues.EnergyOnly(chargePointId, meterStop, OptionalString(call.Payload, "timestamp"), receivedAt) is { } stop)
        {
            readings.Add(stop);
        }

        var reply = new JsonObject { ["idTagInfo"] = AcceptedIdTag.DeepClone() };
        return new OcppOutcome(OcppFrameCodec.WriteCallResult(call.UniqueId, reply), Reported(readings));
    }

    // The schema allows any integer; one outside int's range is certainly not connector 0 or 1.
    private static bool AppliesToDevice(JsonElement payload) =>
        payload.GetProperty("connectorId").TryGetInt32(out var connectorId) && OcppStatusMapping.AppliesToDevice(connectorId);

    private static DeviceEvent[] Reported(IEnumerable<DeviceReading> readings) =>
        readings.Select(r => (DeviceEvent)new DeviceEvent.ReadingReported(r)).ToArray();

    private OcppOutcome Boot(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        var reply = new JsonObject
        {
            ["status"] = "Accepted",
            ["currentTime"] = Time(receivedAt),
            ["interval"] = _intervalSeconds,
        };

        return new OcppOutcome(
            OcppFrameCodec.WriteCallResult(call.UniqueId, reply),
            [new DeviceEvent.Connected(chargePointId, receivedAt, Hardware(call.Payload))]);
    }

    // The schema allows an empty vendor or model; a charger that sends one has told us nothing usable.
    private static DeviceHardware? Hardware(JsonElement payload)
    {
        var vendor = payload.GetProperty("chargePointVendor").GetString();
        var model = payload.GetProperty("chargePointModel").GetString();
        return string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(model)
            ? null
            : new DeviceHardware(vendor, model, OptionalString(payload, "firmwareVersion"));
    }

    private static OcppOutcome Status(OcppCall call, string chargePointId, DateTimeOffset receivedAt)
    {
        var reply = OcppFrameCodec.WriteCallResult(call.UniqueId, []);
        if (!AppliesToDevice(call.Payload))
        {
            return new OcppOutcome(reply);
        }

        var status = OcppStatusMapping.Map(call.Payload.GetProperty("status").GetString()!);
        var at = OcppStatusMapping.ReportedTime(OptionalString(call.Payload, "timestamp"), receivedAt);
        return new OcppOutcome(reply, [new DeviceEvent.StatusChanged(chargePointId, at, status)]);
    }
}
