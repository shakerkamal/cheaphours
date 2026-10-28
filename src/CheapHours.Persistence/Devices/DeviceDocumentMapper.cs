using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Devices;

/// <summary>
/// Turns a <c>devices</c> document into the Core <see cref="Device"/>, including its last known state.
/// Only our code writes these documents, so a document that breaks a Core invariant throws: it is a bug.
/// </summary>
internal static class DeviceDocumentMapper
{
    private static readonly Capabilities[] KnownCapabilities = Enum.GetValues<Capabilities>().Where(c => c != Capabilities.None).ToArray();

    /// <summary>Maps a stored document.</summary>
    /// <param name="d">The document.</param>
    /// <returns>The device with its state.</returns>
    public static Device ToDevice(DeviceDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);

        var state = new DeviceState(
            d.Status,
            UtcTime.FromBson(d.StatusAt),
            UtcTime.FromBson(d.LastSeenAt),
            d.ReportIntervalSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null,
            d.Hardware is { } h ? new DeviceHardware(h.Manufacturer, h.Model, h.FirmwareVersion) : null,
            d.LastReading is { } r ? new DeviceReading(d.ExternalId, UtcTime.FromBson(r.Ts), r.PowerW, r.EnergyWh, (decimal?)r.SocPercent) : null);

        return new Device(
            DeviceId.Parse(d.Id),
            OwnerId.Parse(d.OwnerId),
            d.ProviderId,
            d.ExternalId,
            d.Type,
            Flags(d.Capabilities),
            new DeviceAttributes(d.Attributes.BatteryCapacityWh, d.Attributes.MaxPowerW),
            state);
    }

    /// <summary>Returns the stored names of the flags set in <paramref name="capabilities"/>.</summary>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>One name per set flag.</returns>
    public static List<string> Names(Capabilities capabilities) =>
        KnownCapabilities.Where(c => capabilities.HasFlag(c)).Select(c => c.ToString()).ToList();

    // Names no longer defined are ignored rather than failing the read.
    private static Capabilities Flags(IEnumerable<string> names) =>
        names.Aggregate(Capabilities.None, (all, name) => Enum.TryParse<Capabilities>(name, out var c) ? all | c : all);
}
