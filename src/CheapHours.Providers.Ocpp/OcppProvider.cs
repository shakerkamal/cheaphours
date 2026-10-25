using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.Providers.Ocpp;

/// <summary>
/// The OCPP 1.6J provider: chargers push frames over the gateway's WebSocket, and this translates them into
/// normalised device events. Command sending arrives with command dispatch.
/// </summary>
public sealed class OcppProvider : IInboundTranslator
{
    /// <summary>The provider id.</summary>
    public const string Id = "ocpp";

    // Translation ignores the reply, so the interval it would carry does not matter here.
    private static readonly TimeSpan UnusedInterval = TimeSpan.FromSeconds(60);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly OcppCallHandler _handler;

    /// <summary>Creates the provider.</summary>
    /// <param name="validator">The OCPP 1.6 schema validator.</param>
    public OcppProvider(OcppSchemaValidator validator) => _handler = new OcppCallHandler(validator, UnusedInterval);

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public Capabilities Capabilities => Capabilities.PushesEvents;

    /// <inheritdoc/>
    public IReadOnlyList<DeviceEvent> Translate(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var at = message.ReceivedAt;
        if (string.IsNullOrWhiteSpace(message.ExternalId))
        {
            return [new DeviceEvent.MalformedInput(at, "the frame has no charge point id")];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(message.Body.Span);
        }
        catch (DecoderFallbackException)
        {
            return [new DeviceEvent.MalformedInput(at, "the frame is not valid UTF-8")];
        }

        var frame = OcppFrameCodec.Decode(text);
        if (frame is OcppFrameError error)
        {
            return [new DeviceEvent.MalformedInput(at, error.Reason)];
        }

        var outcome = _handler.Handle(frame, at);
        return outcome switch
        {
            { Error: OcppErrorCode.OccurenceConstraintViolation or OcppErrorCode.TypeConstraintViolation or OcppErrorCode.PropertyConstraintViolation } =>
                [new DeviceEvent.MalformedInput(at, "the payload violates the OCPP 1.6 schema")],
            { Effect: BootReported } => [new DeviceEvent.Connected(message.ExternalId, at)],
            { Effect: StatusReported status } => [new DeviceEvent.StatusChanged(message.ExternalId, status.At, status.Status)],
            _ => [],
        };
    }
}
