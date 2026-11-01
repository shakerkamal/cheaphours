using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.ContractTests.Broken;

/// <summary>A deliberately broken provider: it throws on a vendor failure instead of returning it.</summary>
internal sealed class ThrowsOnServerErrorProvider(ProviderHttpClient<ThrowsOnServerErrorProvider> vendor) : IPollingProvider, ICommandSender
{
    public string ProviderId => "throwsonerror";

    public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.StartStop;

    public async Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        using var response = await vendor.SendAsync(account, () => new HttpRequestMessage(HttpMethod.Get, "api/devices"), cancellationToken);
        if (response is VendorResponse.Failed failed)
        {
            throw new HttpRequestException(failed.Failure.Reason); // the bug: vendor failures must be returned
        }

        var body = await ((VendorResponse.Succeeded)response).Response.Content.ReadAsStringAsync(cancellationToken);
        return new PollResult.Completed(BrokenCloudReadings.Parse(body));
    }

    public async Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken)
    {
        if (!Capabilities.HasFlag(command.RequiredCapability))
        {
            return new SendResult.Rejected("capability-not-supported");
        }

        using var response = await vendor.SendAsync(address.Account!, BrokenCloudReadings.Start(command, address), cancellationToken);
        return response is VendorResponse.Failed failed
            ? throw new HttpRequestException(failed.Failure.Reason) // the bug again
            : new SendResult.Accepted();
    }
}
