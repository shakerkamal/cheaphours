using CheapHours.Core.Commands;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;

namespace CheapHours.Host.Commands;

/// <summary>
/// Sends a command through the device's provider when that provider is a registered <see cref="ICommandSender"/>
/// (a vendor cloud), using the owner's linked account at that provider. The result comes back at once.
/// </summary>
internal sealed class ProviderCommandRoute(IEnumerable<ICommandSender> senders, ProviderAccountStore accounts) : ICommandRoute
{
    private readonly Dictionary<string, ICommandSender> _senders = senders.ToDictionary(s => s.ProviderId, StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool Reaches(StoredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _senders.ContainsKey(device.Device.ProviderId);
    }

    /// <inheritdoc/>
    public async Task<RouteOutcome> SendAsync(StoredCommand command, StoredDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(device);
        var sender = _senders[device.Device.ProviderId];
        if (await accounts.FindAsync(command.OwnerId, sender.ProviderId, cancellationToken).ConfigureAwait(false) is not { } account)
        {
            return new RouteOutcome.Completed(new SendResult.Rejected("account-not-linked"));
        }

        var result = await sender.SendAsync(command.Command.Request, new DeviceAddress(device.Device.ExternalId, account), cancellationToken).ConfigureAwait(false);
        return new RouteOutcome.Completed(result);
    }
}
