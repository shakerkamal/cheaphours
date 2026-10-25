using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// What handling one frame produced: the reply to send (if any) and the effect to store before sending it.
/// The gateway applies <see cref="Effect"/> first and sends <see cref="Reply"/> only if that succeeded.
/// </summary>
/// <param name="Reply">The frame to send back, or <see langword="null"/> when nothing is sent (results and errors from the charger).</param>
/// <param name="Effect">The device change to store, if any.</param>
/// <param name="Error">The CallError code when <paramref name="Reply"/> is a CallError; for logs.</param>
public sealed record OcppOutcome(string? Reply, OcppEffect? Effect = null, OcppErrorCode? Error = null)
{
    /// <summary>Gets the outcome that sends nothing and changes nothing.</summary>
    public static OcppOutcome None { get; } = new(Reply: null);
}

/// <summary>A device change a frame asks for. The set is closed.</summary>
public abstract record OcppEffect
{
    private protected OcppEffect()
    {
    }
}

/// <summary>A <c>BootNotification</c> reported the charger's hardware.</summary>
/// <param name="Manufacturer">The reported <c>chargePointVendor</c>.</param>
/// <param name="Model">The reported <c>chargePointModel</c>.</param>
/// <param name="FirmwareVersion">The reported <c>firmwareVersion</c>, if any.</param>
public sealed record BootReported(string Manufacturer, string Model, string? FirmwareVersion) : OcppEffect;

/// <summary>A <c>StatusNotification</c> for the charger set its status.</summary>
/// <param name="Status">The normalised status.</param>
/// <param name="At">When the status applies from (UTC).</param>
public sealed record StatusReported(DeviceStatus Status, DateTimeOffset At) : OcppEffect;
