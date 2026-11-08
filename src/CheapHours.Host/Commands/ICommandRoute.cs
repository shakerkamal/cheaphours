using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;

namespace CheapHours.Host.Commands;

/// <summary>One way of reaching devices (command-dispatch design §6): a vendor cloud's sender, or a gateway replica.</summary>
internal interface ICommandRoute
{
    /// <summary>Returns whether this route reaches <paramref name="device"/>.</summary>
    /// <param name="device">The device.</param>
    /// <returns><see langword="true"/> when the route can carry the device's commands.</returns>
    bool Reaches(StoredDevice device);

    /// <summary>Hands <paramref name="command"/> to the route. Vendor failures come back as transient or rejected, never thrown.</summary>
    /// <param name="command">The claimed command.</param>
    /// <param name="device">Its device.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The outcome, now or later.</returns>
    Task<RouteOutcome> SendAsync(StoredCommand command, StoredDevice device, CancellationToken cancellationToken);
}
