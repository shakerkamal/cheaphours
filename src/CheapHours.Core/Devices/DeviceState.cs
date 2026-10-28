namespace CheapHours.Core.Devices;

/// <summary>
/// What is last known about a device: what it reported and when it was last heard from. Identity and
/// attributes live on <see cref="Device"/>; this is the part that changes as the device reports.
/// </summary>
public sealed record DeviceState
{
    /// <summary>Creates a device state.</summary>
    /// <param name="status">The last status.</param>
    /// <param name="statusAt">When that status applies from (UTC).</param>
    /// <param name="lastSeenAt">When anything was last received from the device (UTC), or <see langword="null"/> if never.</param>
    /// <param name="reportInterval">How often the device is expected to report, or <see langword="null"/> when unknown; greater than zero.</param>
    /// <param name="hardware">What the device reported about itself, if anything.</param>
    /// <param name="lastReading">The latest reading, if any.</param>
    /// <exception cref="ArgumentException">A time is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The status is undefined or the interval is not greater than zero.</exception>
    public DeviceState(
        DeviceStatus status,
        DateTimeOffset statusAt,
        DateTimeOffset? lastSeenAt = null,
        TimeSpan? reportInterval = null,
        DeviceHardware? hardware = null,
        DeviceReading? lastReading = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown device status.");
        }

        if (reportInterval is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(reportInterval));
        }

        Status = status;
        StatusAt = Guard.Utc(statusAt);
        LastSeenAt = Guard.Utc(lastSeenAt);
        ReportInterval = reportInterval;
        Hardware = hardware;
        LastReading = lastReading;
    }

    /// <summary>Gets the last status.</summary>
    public DeviceStatus Status { get; }

    /// <summary>Gets when the status applies from (UTC).</summary>
    public DateTimeOffset StatusAt { get; }

    /// <summary>Gets when anything was last received from the device (UTC), or <see langword="null"/> if never.</summary>
    public DateTimeOffset? LastSeenAt { get; }

    /// <summary>Gets how often the device is expected to report, or <see langword="null"/> when unknown.</summary>
    public TimeSpan? ReportInterval { get; }

    /// <summary>Gets what the device reported about itself, or <see langword="null"/>.</summary>
    public DeviceHardware? Hardware { get; }

    /// <summary>Gets the latest reading, or <see langword="null"/> when none was stored.</summary>
    public DeviceReading? LastReading { get; }

    /// <summary>
    /// Returns the status as of <paramref name="now"/>: <see cref="DeviceStatus.Offline"/> when the report
    /// interval is known and the device has been silent for more than <see cref="DevicePresence.MissedIntervalsAllowed"/>
    /// intervals since it was last heard from (its last-seen time, else its status time); otherwise the stored status.
    /// </summary>
    /// <param name="now">The current time (UTC), from an injected clock.</param>
    /// <returns>The status to show.</returns>
    /// <exception cref="ArgumentException"><paramref name="now"/> is not UTC.</exception>
    public DeviceStatus StatusAsOf(DateTimeOffset now)
    {
        Guard.Utc(now);
        return ReportInterval is { } interval && !DevicePresence.IsOnline(LastSeenAt ?? StatusAt, interval, now)
            ? DeviceStatus.Offline
            : Status;
    }
}
