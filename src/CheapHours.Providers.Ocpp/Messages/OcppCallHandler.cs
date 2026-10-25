using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// Turns one frame from a charger into an <see cref="OcppOutcome"/>. Pure: no I/O and no clock; the
/// receipt time is passed in, and stores are written by the caller.
/// </summary>
public sealed class OcppCallHandler
{
    /// <summary>The OCPP-J timestamp format for times we send.</summary>
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string InternalErrorDescription = "The central system could not process the request.";

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
    /// <param name="receivedAt">When the gateway received it (UTC); also the <c>currentTime</c> it reports.</param>
    /// <returns>The reply and effect.</returns>
    public OcppOutcome Handle(OcppFrame frame, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame switch
        {
            OcppCall call => HandleCall(call, receivedAt.ToUniversalTime()),
            OcppFrameError error => new OcppOutcome(OcppFrameCodec.WriteFormationViolation(error), Error: OcppErrorCode.FormationViolation),

            // The gateway sends no calls yet, so any result or error from the charger is unsolicited.
            _ => OcppOutcome.None,
        };
    }

    /// <summary>The CallError sent when storing a valid call's effect failed.</summary>
    /// <param name="uniqueId">The call's id.</param>
    /// <returns>An <c>InternalError</c> frame with no internal details.</returns>
    public static string InternalErrorReply(string uniqueId) =>
        OcppFrameCodec.WriteCallError(uniqueId, OcppErrorCode.InternalError, InternalErrorDescription);

    private static OcppOutcome Refuse(string uniqueId, OcppErrorCode code) =>
        new(OcppFrameCodec.WriteCallError(uniqueId, code, Descriptions[code]), Error: code);

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) ? value.GetString() : null;

    private OcppOutcome HandleCall(OcppCall call, DateTimeOffset receivedAt)
    {
        if (!OcppActions.IsKnown(call.Action))
        {
            return Refuse(call.UniqueId, OcppErrorCode.NotImplemented);
        }

        if (call.Action is not (OcppActions.BootNotification or OcppActions.Heartbeat or OcppActions.StatusNotification))
        {
            return Refuse(call.UniqueId, OcppErrorCode.NotSupported);
        }

        if (_validator.ValidateRequest(call.Action, call.Payload) is { } violation)
        {
            return Refuse(call.UniqueId, violation);
        }

        return call.Action switch
        {
            OcppActions.BootNotification => Boot(call, receivedAt),
            OcppActions.Heartbeat => new OcppOutcome(OcppFrameCodec.WriteCallResult(call.UniqueId, new JsonObject { ["currentTime"] = Time(receivedAt) })),
            _ => Status(call, receivedAt),
        };
    }

    private OcppOutcome Boot(OcppCall call, DateTimeOffset receivedAt)
    {
        var reply = new JsonObject
        {
            ["status"] = "Accepted",
            ["currentTime"] = Time(receivedAt),
            ["interval"] = _intervalSeconds,
        };

        var effect = new BootReported(
            call.Payload.GetProperty("chargePointVendor").GetString()!,
            call.Payload.GetProperty("chargePointModel").GetString()!,
            OptionalString(call.Payload, "firmwareVersion"));

        return new OcppOutcome(OcppFrameCodec.WriteCallResult(call.UniqueId, reply), effect);
    }

    private static OcppOutcome Status(OcppCall call, DateTimeOffset receivedAt)
    {
        var reply = OcppFrameCodec.WriteCallResult(call.UniqueId, []);
        // The schema allows any integer; one outside int's range is certainly not connector 0 or 1.
        if (!call.Payload.GetProperty("connectorId").TryGetInt32(out var connectorId) || !OcppStatusMapping.AppliesToDevice(connectorId))
        {
            return new OcppOutcome(reply);
        }

        var status = OcppStatusMapping.Map(call.Payload.GetProperty("status").GetString()!);
        var at = OcppStatusMapping.StatusTime(OptionalString(call.Payload, "timestamp"), receivedAt);
        return new OcppOutcome(reply, new StatusReported(status, at));
    }
}
