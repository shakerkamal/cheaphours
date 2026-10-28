using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// What handling one frame produced: the reply to send (if any) and the normalised device events to record
/// before sending it. The gateway records <see cref="Events"/> first and sends <see cref="Reply"/> only if
/// that succeeded.
/// </summary>
public sealed record OcppOutcome
{
    /// <summary>Creates the outcome.</summary>
    /// <param name="reply">The frame to send back, or <see langword="null"/> when nothing is sent (results and errors from the charger).</param>
    /// <param name="events">The device events the frame reported, in frame order; empty when none.</param>
    /// <param name="error">The CallError code when <paramref name="reply"/> is a CallError; for logs.</param>
    public OcppOutcome(string? reply, IReadOnlyList<DeviceEvent>? events = null, OcppErrorCode? error = null)
    {
        Reply = reply;
        Events = events ?? [];
        Error = error;
    }

    /// <summary>Gets the outcome that sends nothing and reports nothing.</summary>
    public static OcppOutcome None { get; } = new(reply: null);

    /// <summary>Gets the frame to send back, or <see langword="null"/> when nothing is sent.</summary>
    public string? Reply { get; }

    /// <summary>Gets the device events the frame reported, in frame order.</summary>
    public IReadOnlyList<DeviceEvent> Events { get; }

    /// <summary>Gets the CallError code when <see cref="Reply"/> is a CallError.</summary>
    public OcppErrorCode? Error { get; }
}
