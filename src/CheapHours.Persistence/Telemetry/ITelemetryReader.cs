using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Telemetry;

/// <summary>Reads a device's telemetry back as 15-minute buckets, aggregated inside MongoDB (ADR-002).</summary>
public interface ITelemetryReader
{
    /// <summary>
    /// Returns the buckets of <paramref name="deviceId"/> whose slots start in
    /// [<paramref name="startAt"/>, <paramref name="endAt"/>), ordered by start; slots without readings are left out.
    /// A bucket's energy adds up, over its readings, each reading's energy minus the previous reading's,
    /// counting an increment only when the previous reading is at most 15 minutes older and its energy is
    /// not higher. The previous reading may lie before <paramref name="startAt"/>.
    /// </summary>
    /// <param name="deviceId">The device. Ownership is the caller's concern.</param>
    /// <param name="startAt">The first slot start (UTC, on the 15-minute grid).</param>
    /// <param name="endAt">The end of the range (UTC, on the grid, after <paramref name="startAt"/>).</param>
    /// <param name="take">The most buckets to return; greater than zero.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="take"/> buckets.</returns>
    Task<IReadOnlyList<TelemetryBucket>> GetBucketsAsync(DeviceId deviceId, DateTimeOffset startAt, DateTimeOffset endAt, int take, CancellationToken cancellationToken);
}
