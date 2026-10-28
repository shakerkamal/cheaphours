using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Devices;

/// <summary>
/// The <c>devices</c> collection as seen by the public API. Every query is scoped to one owner inside the
/// database filter, so another owner's device never leaves the database (D3).
/// </summary>
public interface IDeviceReader
{
    /// <summary>Reads one of <paramref name="ownerId"/>'s devices.</summary>
    /// <param name="ownerId">The owner of the calling API key.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The device with its state, or <see langword="null"/> when it does not exist or belongs to another owner.</returns>
    Task<Device?> GetAsync(OwnerId ownerId, DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Lists <paramref name="ownerId"/>'s devices, oldest first (device ids are time-ordered).</summary>
    /// <param name="ownerId">The owner of the calling API key.</param>
    /// <param name="after">Continue after this device id, or <see langword="null"/> to start at the oldest.</param>
    /// <param name="take">The most devices to return; greater than zero.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="take"/> devices.</returns>
    Task<IReadOnlyList<Device>> ListAsync(OwnerId ownerId, DeviceId? after, int take, CancellationToken cancellationToken);
}
