using CheapHours.Core.Ids;

namespace CheapHours.Core.Goals;

/// <summary>
/// What the driver wants: a target state of charge by a departure time. A goal links two devices (D2):
/// the vehicle reports state of charge and battery capacity, the charger takes the commands.
/// </summary>
public sealed record Goal
{
    /// <summary>Creates a goal.</summary>
    /// <param name="id">The goal's id.</param>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="vehicleDeviceId">The vehicle to charge.</param>
    /// <param name="chargerDeviceId">The charger to command.</param>
    /// <param name="targetSocPercent">The target state of charge, 1–100 percent.</param>
    /// <param name="departureUtc">When the car must be ready, in UTC.</param>
    /// <exception cref="ArgumentException">A value is missing or out of range, or the same device is given twice.</exception>
    public Goal(GoalId id, OwnerId ownerId, DeviceId vehicleDeviceId, DeviceId chargerDeviceId, int targetSocPercent, DateTimeOffset departureUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetSocPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetSocPercent, 100);
        if (Guard.NotDefault(vehicleDeviceId) == Guard.NotDefault(chargerDeviceId))
        {
            throw new ArgumentException("The vehicle and the charger must be different devices.", nameof(chargerDeviceId));
        }

        Id = Guard.NotDefault(id);
        OwnerId = Guard.NotDefault(ownerId);
        VehicleDeviceId = vehicleDeviceId;
        ChargerDeviceId = chargerDeviceId;
        TargetSocPercent = targetSocPercent;
        DepartureUtc = Guard.Utc(departureUtc);
    }

    /// <summary>Gets the goal's id.</summary>
    public GoalId Id { get; }

    /// <summary>Gets the owner.</summary>
    public OwnerId OwnerId { get; }

    /// <summary>Gets the vehicle to charge.</summary>
    public DeviceId VehicleDeviceId { get; }

    /// <summary>Gets the charger to command.</summary>
    public DeviceId ChargerDeviceId { get; }

    /// <summary>Gets the target state of charge in percent.</summary>
    public int TargetSocPercent { get; }

    /// <summary>Gets the departure time (UTC).</summary>
    public DateTimeOffset DepartureUtc { get; }
}
