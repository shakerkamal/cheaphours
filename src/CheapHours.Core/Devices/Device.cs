using CheapHours.Core.Ids;

namespace CheapHours.Core.Devices;

/// <summary>One device in the normalised device model, owned by one owner and reached through one provider.</summary>
public sealed record Device
{
    /// <summary>Creates a device.</summary>
    /// <param name="id">Our id for the device.</param>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="providerId">The provider that reaches it, for example <c>ocpp</c>.</param>
    /// <param name="externalId">The vendor's id for the device; unique per provider.</param>
    /// <param name="type">What kind of appliance it is.</param>
    /// <param name="capabilities">What it can do.</param>
    /// <param name="attributes">Typed attributes (D2): chargers need a maximum power, vehicles a battery capacity.</param>
    /// <param name="state">What is last known about the device, or <see langword="null"/> for a device that has not reported yet.</param>
    /// <exception cref="ArgumentException">A value is missing or malformed, a required attribute is absent, or the last reading belongs to another external id.</exception>
    public Device(DeviceId id, OwnerId ownerId, string providerId, string externalId, DeviceType type, Capabilities capabilities, DeviceAttributes attributes, DeviceState? state = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (!ProviderIdFormat.IsValid(providerId))
        {
            throw new ArgumentException($"'{providerId}' is not a valid provider id (1–{ProviderIdFormat.MaxLength} lowercase letters or digits).", nameof(providerId));
        }

        if (type == DeviceType.Charger && attributes.MaxPowerW is null)
        {
            throw new ArgumentException("A charger must have a maximum power.", nameof(attributes));
        }

        if (type == DeviceType.Vehicle && attributes.BatteryCapacityWh is null)
        {
            throw new ArgumentException("A vehicle must have a battery capacity.", nameof(attributes));
        }

        if (state?.LastReading is { } reading && reading.ExternalId != externalId)
        {
            throw new ArgumentException($"The last reading belongs to external id '{reading.ExternalId}', not '{externalId}'.", nameof(state));
        }

        Id = Guard.NotDefault(id);
        OwnerId = Guard.NotDefault(ownerId);
        ProviderId = providerId;
        ExternalId = Guard.NotBlank(externalId);
        Type = type;
        Capabilities = capabilities;
        Attributes = attributes;
        State = state;
    }

    /// <summary>Gets our id for the device.</summary>
    public DeviceId Id { get; }

    /// <summary>Gets the owner.</summary>
    public OwnerId OwnerId { get; }

    /// <summary>Gets the provider id.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the vendor's id for the device.</summary>
    public string ExternalId { get; }

    /// <summary>Gets the kind of appliance.</summary>
    public DeviceType Type { get; }

    /// <summary>Gets what the device can do.</summary>
    public Capabilities Capabilities { get; }

    /// <summary>Gets the typed attributes.</summary>
    public DeviceAttributes Attributes { get; }

    /// <summary>Gets what is last known about the device, or <see langword="null"/> when it has not reported yet.</summary>
    public DeviceState? State { get; }

    /// <summary>Gets the stored status: <see cref="DeviceStatus.Unknown"/> for a device without a state.</summary>
    public DeviceStatus Status => State?.Status ?? DeviceStatus.Unknown;

    /// <summary>Returns the status to show at <paramref name="now"/>, applying <see cref="DeviceState.StatusAsOf"/>.</summary>
    /// <param name="now">The current time (UTC), from an injected clock.</param>
    /// <returns>The status to show.</returns>
    /// <exception cref="ArgumentException"><paramref name="now"/> is not UTC.</exception>
    public DeviceStatus StatusAsOf(DateTimeOffset now)
    {
        Guard.Utc(now);
        return State?.StatusAsOf(now) ?? DeviceStatus.Unknown;
    }
}
