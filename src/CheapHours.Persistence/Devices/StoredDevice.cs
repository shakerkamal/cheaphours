using CheapHours.Core.Devices;

namespace CheapHours.Persistence.Devices;

/// <summary>A device with the stored facts that commands need beyond the Core model.</summary>
/// <param name="Device">The device and its last known state.</param>
/// <param name="StatusReceivedAt">When CheapHours received the latest reported status (UTC), if any.</param>
/// <param name="ActiveSessionId">The device's current charging session as its vendor names it, if one runs.</param>
/// <param name="RemovedAt">When the vendor reported the device as removed, if it did.</param>
public sealed record StoredDevice(Device Device, DateTimeOffset? StatusReceivedAt, string? ActiveSessionId, DateTimeOffset? RemovedAt);
