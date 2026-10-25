using System.Collections.Frozen;
using System.Globalization;
using CheapHours.Core.Devices;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>Maps an OCPP 1.6 <c>StatusNotification</c> onto the normalised device status.</summary>
public static class OcppStatusMapping
{
    // Every OCPP 1.6 ChargePointStatus. "Connected but not delivering power" states count as Available;
    // Unavailable (taken out of service, still connected) has no normalised meaning, so it is Unknown.
    private static readonly FrozenDictionary<string, DeviceStatus> Statuses = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal)
    {
        ["Available"] = DeviceStatus.Available,
        ["Preparing"] = DeviceStatus.Available,
        ["SuspendedEV"] = DeviceStatus.Available,
        ["SuspendedEVSE"] = DeviceStatus.Available,
        ["Finishing"] = DeviceStatus.Available,
        ["Reserved"] = DeviceStatus.Available,
        ["Charging"] = DeviceStatus.Charging,
        ["Faulted"] = DeviceStatus.Faulted,
        ["Unavailable"] = DeviceStatus.Unknown,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets every OCPP 1.6 <c>ChargePointStatus</c> value this mapping covers.</summary>
    public static IReadOnlyCollection<string> OcppStatuses => Statuses.Keys;

    /// <summary>Maps an OCPP 1.6 <c>ChargePointStatus</c> to a <see cref="DeviceStatus"/>.</summary>
    /// <param name="ocppStatus">A status value the schema allows.</param>
    /// <returns>The device status.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not an OCPP 1.6 status (payloads are validated first).</exception>
    public static DeviceStatus Map(string ocppStatus) =>
        Statuses.TryGetValue(ocppStatus, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(ocppStatus), ocppStatus, "Not an OCPP 1.6 ChargePointStatus.");

    /// <summary>Returns whether a notification for <paramref name="connectorId"/> sets the device status.</summary>
    /// <param name="connectorId">0 is the charge point itself; 1 is its first connector.</param>
    /// <returns><see langword="true"/> for connector 0 or 1.</returns>
    public static bool AppliesToDevice(int connectorId) => connectorId is 0 or 1;

    /// <summary>The time a status applies from: the reported timestamp, unless missing, unreadable or after receipt.</summary>
    /// <param name="reportedTimestamp">The payload's <c>timestamp</c>, if any.</param>
    /// <param name="receivedAt">When the gateway received the frame (UTC).</param>
    /// <returns>The status time, in UTC.</returns>
    public static DateTimeOffset StatusTime(string? reportedTimestamp, DateTimeOffset receivedAt)
    {
        // A charger's clock is untrusted: a time after receipt would let it block later updates.
        if (reportedTimestamp is not null
            && DateTimeOffset.TryParse(reportedTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reported)
            && reported <= receivedAt)
        {
            return reported.ToUniversalTime();
        }

        return receivedAt;
    }
}
