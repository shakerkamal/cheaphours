using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Devices;

/// <summary>A device as stored: its identity plus its last known status and when it was last heard from.</summary>
/// <param name="Id">Our id.</param>
/// <param name="OwnerId">The owner.</param>
/// <param name="ProviderId">The provider that reaches it.</param>
/// <param name="ExternalId">The vendor's id.</param>
/// <param name="Type">The kind of appliance.</param>
/// <param name="Capabilities">What it can do.</param>
/// <param name="MaxPowerW">The maximum power, for chargers.</param>
/// <param name="Hardware">What it reported about itself, if anything.</param>
/// <param name="Status">The last status.</param>
/// <param name="StatusAt">When that status applies from (UTC).</param>
/// <param name="LastSeenAt">When anything was last received from it (UTC), if ever.</param>
/// <param name="CreatedAt">When the document was created (UTC).</param>
public sealed record DeviceState(
    DeviceId Id,
    OwnerId OwnerId,
    string ProviderId,
    string ExternalId,
    DeviceType Type,
    Capabilities Capabilities,
    int? MaxPowerW,
    DeviceHardware? Hardware,
    DeviceStatus Status,
    DateTimeOffset StatusAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);
