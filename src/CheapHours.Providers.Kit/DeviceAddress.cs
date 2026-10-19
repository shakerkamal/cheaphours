namespace CheapHours.Providers.Kit;

/// <summary>
/// Where a command must go, in the vendor's terms. The core resolves this from the device record, so a
/// provider never needs to look devices up itself.
/// </summary>
/// <param name="ExternalId">The vendor's id for the device, for example an OCPP charge point id.</param>
/// <param name="Account">The vendor-cloud account to act through, or <see langword="null"/> for devices that connect to us directly.</param>
public sealed record DeviceAddress(string ExternalId, ProviderAccount? Account = null);
