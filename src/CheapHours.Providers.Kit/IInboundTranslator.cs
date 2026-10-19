using CheapHours.Core.Devices;

namespace CheapHours.Providers.Kit;

/// <summary>
/// A provider whose devices or vendor push to us: the gateway hands it raw input (an OCPP frame, a webhook
/// body) to turn into normalized events. Providers implementing this must declare
/// <see cref="Capabilities.PushesEvents"/>.
/// </summary>
public interface IInboundTranslator : IDeviceProvider
{
    /// <summary>Translates raw input into normalised device events.</summary>
    /// <param name="message">The raw input. For webhooks, its signature has already been checked by <see cref="IWebhookVerifier"/>.</param>
    /// <returns>The events, possibly none.</returns>
    /// <remarks>
    /// Contract: pure and total.
    /// <list type="bullet">
    /// <item>Performs no I/O. It runs on the gateway's request path and must not block on anything.</item>
    /// <item>Input of a known kind yields readings and events in W, Wh, percent and UTC.</item>
    /// <item>Input of a kind it does not handle yields an empty list.</item>
    /// <item>Malformed input yields a single <see cref="DeviceEvent.MalformedInput"/> and never throws.</item>
    /// </list>
    /// </remarks>
    IReadOnlyList<DeviceEvent> Translate(InboundMessage message);
}
