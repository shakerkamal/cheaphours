using CheapHours.Core.Devices;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Options;

namespace CheapHours.ContractTests.Broken;

/// <summary>A deliberately broken provider: it translates heat-pump webhooks correctly but accepts every signature.</summary>
internal sealed class AcceptsEverythingVerifierProvider(IOptions<HeatPumpCloudOptions> options) : IInboundTranslator, IWebhookVerifier
{
    private readonly HeatPumpCloudProvider _translator = new(options);

    public string ProviderId => "acceptseverything";

    public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.PushesEvents;

    public IReadOnlyList<DeviceEvent> Translate(InboundMessage message) => _translator.Translate(message);

    public bool Verify(InboundMessage message) => true; // the bug: the signature is never checked
}
