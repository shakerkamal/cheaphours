using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api.Dtos;

/// <summary>A device as clients see it.</summary>
/// <param name="Id">The device id.</param>
/// <param name="ProviderId">The provider that reaches it, for example <c>ocpp</c>.</param>
/// <param name="Type">The kind of appliance.</param>
/// <param name="Capabilities">What it can do.</param>
/// <param name="Status">Its last reported status.</param>
/// <param name="LastSeenAt">When it last reported anything (UTC), if ever.</param>
/// <param name="LastReading">Its most recent reading, if any.</param>
/// <param name="BatteryCapacityWh">Usable battery capacity in Wh (vehicles).</param>
/// <param name="MaxPowerW">Maximum charging power in W (chargers).</param>
internal sealed record DeviceDto(
    DeviceId Id,
    string ProviderId,
    DeviceType Type,
    IReadOnlyList<Capabilities> Capabilities,
    DeviceStatus Status,
    DateTimeOffset? LastSeenAt,
    ReadingDto? LastReading,
    long? BatteryCapacityWh,
    int? MaxPowerW);

/// <summary>One normalised reading.</summary>
/// <param name="Ts">When the device measured it (UTC).</param>
/// <param name="PowerW">Instantaneous power in W.</param>
/// <param name="EnergyWh">Cumulative energy in Wh.</param>
/// <param name="SocPercent">State of charge in percent, if known.</param>
internal sealed record ReadingDto(DateTimeOffset Ts, int PowerW, long EnergyWh, decimal? SocPercent);

/// <summary>One 15-minute telemetry bucket.</summary>
/// <param name="Start">Bucket start (UTC).</param>
/// <param name="End">Bucket end (UTC).</param>
/// <param name="AvgPowerW">Average power over the bucket in W.</param>
/// <param name="EnergyWh">Energy delivered during the bucket in Wh.</param>
/// <param name="SocPercent">State of charge at the end of the bucket, if known.</param>
internal sealed record TelemetryBucketDto(DateTimeOffset Start, DateTimeOffset End, int AvgPowerW, long EnergyWh, decimal? SocPercent);
