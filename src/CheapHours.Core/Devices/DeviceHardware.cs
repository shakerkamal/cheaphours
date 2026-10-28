namespace CheapHours.Core.Devices;

/// <summary>What a device says it is: maker, model and, if known, firmware. Any provider can report it.</summary>
public sealed record DeviceHardware
{
    /// <summary>Creates the description.</summary>
    /// <param name="manufacturer">The maker; not blank.</param>
    /// <param name="model">The model; not blank.</param>
    /// <param name="firmwareVersion">The firmware version, or <see langword="null"/> when not reported.</param>
    /// <exception cref="ArgumentException">The manufacturer or model is blank.</exception>
    public DeviceHardware(string manufacturer, string model, string? firmwareVersion = null)
    {
        Manufacturer = Guard.NotBlank(manufacturer);
        Model = Guard.NotBlank(model);
        FirmwareVersion = firmwareVersion;
    }

    /// <summary>Gets the maker.</summary>
    public string Manufacturer { get; }

    /// <summary>Gets the model.</summary>
    public string Model { get; }

    /// <summary>Gets the firmware version, or <see langword="null"/> when not reported.</summary>
    public string? FirmwareVersion { get; }
}
