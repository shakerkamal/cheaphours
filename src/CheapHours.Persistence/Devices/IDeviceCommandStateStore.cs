using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Devices;

/// <summary>
/// The device facts that sending and confirming commands rely on: when the latest status was received, the
/// active charging session the gateway tracks, and whether the vendor removed the device.
/// </summary>
public interface IDeviceCommandStateStore
{
    /// <summary>Reads a device by id; for the trusted worker only.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The device, or <see langword="null"/>.</returns>
    Task<StoredDevice?> GetAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Reads one of <paramref name="ownerId"/>'s devices (D3).</summary>
    /// <param name="ownerId">The owner of the calling API key.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The device, or <see langword="null"/> when it does not exist or belongs to another owner.</returns>
    Task<StoredDevice?> GetAsync(OwnerId ownerId, DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Reads a device by provider and external id.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The device, or <see langword="null"/>.</returns>
    Task<StoredDevice?> FindAsync(string providerId, string externalId, CancellationToken cancellationToken);

    /// <summary>Records the device's running charging session; a later session replaces it.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="sessionId">The vendor's session id, for example an OCPP transaction id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task SetActiveSessionAsync(string providerId, string externalId, string sessionId, CancellationToken cancellationToken);

    /// <summary>Clears the running session if it is <paramref name="sessionId"/>; another session is left alone.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="sessionId">The session that ended, or <see langword="null"/> when the device reports that none runs.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when it was cleared.</returns>
    Task<bool> ClearActiveSessionAsync(string providerId, string externalId, string? sessionId, CancellationToken cancellationToken);
}
