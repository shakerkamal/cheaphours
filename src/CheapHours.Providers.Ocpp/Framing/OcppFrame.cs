using System.Text.Json;

namespace CheapHours.Providers.Ocpp.Framing;

/// <summary>
/// One OCPP-J message as read from the WebSocket. The set is closed: a frame is a <see cref="OcppCall"/>,
/// <see cref="OcppCallResult"/>, <see cref="OcppCallError"/>, or a <see cref="OcppFrameError"/> when the
/// text was not a valid OCPP-J message.
/// </summary>
public abstract record OcppFrame
{
    private protected OcppFrame()
    {
    }
}

/// <summary>A request: <c>[2, uniqueId, action, payload]</c>.</summary>
/// <param name="UniqueId">The id the answer must carry (1–36 characters).</param>
/// <param name="Action">The OCPP action name, for example <c>BootNotification</c>.</param>
/// <param name="Payload">The payload object, detached from the parsed document.</param>
public sealed record OcppCall(string UniqueId, string Action, JsonElement Payload) : OcppFrame;

/// <summary>A successful answer: <c>[3, uniqueId, payload]</c>.</summary>
/// <param name="UniqueId">The id of the call it answers.</param>
/// <param name="Payload">The payload object, detached from the parsed document.</param>
public sealed record OcppCallResult(string UniqueId, JsonElement Payload) : OcppFrame;

/// <summary>An error answer: <c>[4, uniqueId, errorCode, errorDescription, errorDetails]</c>.</summary>
/// <param name="UniqueId">The id of the call it answers.</param>
/// <param name="ErrorCode">The OCPP error code as sent.</param>
/// <param name="ErrorDescription">The free-text description as sent.</param>
/// <param name="ErrorDetails">The details object, detached from the parsed document.</param>
public sealed record OcppCallError(string UniqueId, string ErrorCode, string ErrorDescription, JsonElement ErrorDetails) : OcppFrame;

/// <summary>Text that is not a valid OCPP-J message; answered with a <c>FormationViolation</c>.</summary>
/// <param name="UniqueId">The frame's uniqueId when one could be read, otherwise <see langword="null"/>.</param>
/// <param name="Reason">What was wrong, for logs; never contains the frame's content.</param>
public sealed record OcppFrameError(string? UniqueId, string Reason) : OcppFrame;
