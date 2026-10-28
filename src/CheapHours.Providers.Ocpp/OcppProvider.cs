using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;
using Microsoft.Extensions.Options;

namespace CheapHours.Providers.Ocpp;

/// <summary>
/// The OCPP 1.6J provider. Chargers push frames over the gateway's WebSocket; the gateway calls
/// <see cref="Handle"/>, which gives the reply and the normalised events, and <see cref="Translate"/> gives
/// the same events for the adapter kit. Command sending arrives with command dispatch.
/// </summary>
public sealed class OcppProvider : IInboundTranslator
{
    /// <summary>The provider id.</summary>
    public const string Id = "ocpp";

    /// <summary>The <see cref="DeviceEvent.MalformedInput"/> reason for input without a charge point id.</summary>
    public const string MissingChargePointReason = "the frame has no charge point id";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly OcppCallHandler _handler;

    /// <summary>Creates the provider.</summary>
    /// <param name="validator">The OCPP 1.6 schema validator.</param>
    /// <param name="options">The provider options; their heartbeat interval goes into every boot answer.</param>
    public OcppProvider(OcppSchemaValidator validator, IOptions<OcppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _handler = new OcppCallHandler(validator, options.Value.HeartbeatInterval);
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public Capabilities Capabilities => Capabilities.PushesEvents;

    /// <summary>Decodes a received WebSocket message; bytes that are not UTF-8 become a frame error.</summary>
    /// <param name="bytes">The message as received.</param>
    /// <returns>The frame.</returns>
    public static OcppFrame Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return OcppFrameCodec.Decode(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return new OcppFrameError(null, "not valid UTF-8");
        }
    }

    /// <summary>Handles one frame from a charger: the reply to send and the events to record first.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="chargePointId">The charger it came from.</param>
    /// <param name="receivedAt">When it was received (UTC).</param>
    /// <returns>The reply and the events.</returns>
    public OcppOutcome Handle(OcppFrame frame, string chargePointId, DateTimeOffset receivedAt) =>
        _handler.Handle(frame, chargePointId, receivedAt);

    /// <inheritdoc/>
    public IReadOnlyList<DeviceEvent> Translate(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.ExternalId))
        {
            return [new DeviceEvent.MalformedInput(message.ReceivedAt, MissingChargePointReason)];
        }

        return Handle(Decode(message.Body.Span), message.ExternalId, message.ReceivedAt).Events;
    }
}
