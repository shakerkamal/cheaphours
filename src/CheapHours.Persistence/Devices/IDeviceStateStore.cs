using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Devices;

/// <summary>
/// The <c>devices</c> collection as seen by the gateway: one document per provider and external id, whose
/// status and last-seen time only ever move forward in time.
/// </summary>
public interface IDeviceStateStore
{
    /// <summary>
    /// Creates the device with status <see cref="DeviceStatus.Unknown"/> if no device exists for its provider
    /// and external id; otherwise keeps the existing one. Either way, a device that is
    /// <see cref="DeviceStatus.Offline"/> becomes <see cref="DeviceStatus.Unknown"/> at <paramref name="at"/>.
    /// Safe under concurrent calls: at most one device is created. The device's report interval is set to
    /// <paramref name="reportInterval"/> on every call, so readers can tell when it has gone silent.
    /// </summary>
    /// <param name="device">The device to create if missing; its id is used only on insert.</param>
    /// <param name="reportInterval">How often the device is expected to report (whole seconds, at least 1 s).</param>
    /// <param name="at">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The id of the stored device.</returns>
    Task<DeviceId> EnsureAsync(Device device, TimeSpan reportInterval, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Stores what the device reported about itself.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="hardware">The reported hardware.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when stored.</returns>
    Task RecordHardwareAsync(string providerId, string externalId, DeviceHardware hardware, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a status the device reported, if <paramref name="at"/> is later than the last status it
    /// reported and the device was not set <see cref="DeviceStatus.Offline"/> at or after <paramref name="at"/>.
    /// Statuses the gateway set itself (<see cref="DeviceStatus.Unknown"/> on connect) do not block it.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="status">The reported status.</param>
    /// <param name="at">When the status applies from (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the status changed.</returns>
    Task<bool> RecordStatusAsync(string providerId, string externalId, DeviceStatus status, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Marks the device <see cref="DeviceStatus.Offline"/> if <paramref name="at"/> is later than its current status time.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="at">When the device was found unreachable (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the status changed.</returns>
    Task<bool> MarkOfflineAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Moves <c>lastSeenAt</c> to <paramref name="at"/> if that is later; never backwards.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="at">When something was received from the device (UTC).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when stored.</returns>
    Task RecordSeenAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Reads a device by provider and external id.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="externalId">The vendor's id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The device, or <see langword="null"/>.</returns>
    Task<Device?> FindAsync(string providerId, string externalId, CancellationToken cancellationToken);
}
