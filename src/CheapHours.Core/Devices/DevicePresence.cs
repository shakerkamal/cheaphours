namespace CheapHours.Core.Devices;

/// <summary>Decides whether a device that reports on a fixed interval is still reachable.</summary>
public static class DevicePresence
{
    /// <summary>How many expected reports in a row may be missed before a device counts as offline.</summary>
    public const int MissedIntervalsAllowed = 3;

    /// <summary>
    /// Returns whether a device last heard from at <paramref name="lastSeenAt"/> is online at <paramref name="now"/>:
    /// online until more than <see cref="MissedIntervalsAllowed"/> intervals have passed.
    /// </summary>
    /// <param name="lastSeenAt">When anything was last received from the device (UTC).</param>
    /// <param name="expectedInterval">How often the device is expected to report (an OCPP heartbeat, a poll); greater than zero.</param>
    /// <param name="now">The current time (UTC), from an injected clock.</param>
    /// <returns><see langword="true"/> while the device counts as online.</returns>
    /// <exception cref="ArgumentException">A time is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval is not greater than zero.</exception>
    public static bool IsOnline(DateTimeOffset lastSeenAt, TimeSpan expectedInterval, DateTimeOffset now)
    {
        Guard.Utc(lastSeenAt);
        Guard.Utc(now);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedInterval, TimeSpan.Zero);

        return now - lastSeenAt <= MissedIntervalsAllowed * expectedInterval;
    }
}
