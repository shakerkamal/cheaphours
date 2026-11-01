using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.ContractTests.Broken;

/// <summary>A deliberately broken provider: it declares start/stop only but sends power limits to its vendor anyway.</summary>
internal sealed class SendsUndeclaredCapabilityProvider(ProviderHttpClient<SendsUndeclaredCapabilityProvider> vendor) : ICommandSender
{
    public string ProviderId => "sendsundeclared";

    public Capabilities Capabilities => Capabilities.StartStop;

    public async Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken)
    {
        // The bug: no capability check before the call.
        using var response = await vendor.SendAsync(address.Account!, BrokenCloudReadings.Start(command, address), cancellationToken);
        return response is VendorResponse.Failed failed ? failed.Failure.ToSendResult() : new SendResult.Accepted();
    }
}
