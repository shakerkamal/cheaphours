namespace CheapHours.Persistence.Devices;

/// <summary>What a device reported about itself (for a charger, its boot message).</summary>
/// <param name="Manufacturer">The manufacturer.</param>
/// <param name="Model">The model.</param>
/// <param name="FirmwareVersion">The firmware version, if reported.</param>
public sealed record DeviceHardware(string Manufacturer, string Model, string? FirmwareVersion);
