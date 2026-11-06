using CheapHours.Core.Ids;

namespace CheapHours.Host.Goals;

/// <summary>A goal as requested, already well formed (the edge checked ids, range and UTC).</summary>
/// <param name="VehicleId">The vehicle to charge.</param>
/// <param name="ChargerId">The charger to use.</param>
/// <param name="TargetSocPercent">The target charge, 1–100.</param>
/// <param name="DepartureUtc">The departure (UTC).</param>
internal sealed record GoalRequest(DeviceId VehicleId, DeviceId ChargerId, int TargetSocPercent, DateTimeOffset DepartureUtc);
