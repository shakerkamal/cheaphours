namespace CheapHours.Host.Prices;

/// <summary>
/// When the price import runs and what it fetches (design §8), in Europe/Berlin time. Pure: it is given the
/// time and reads no clock. Runs come every retry interval, but never later than the next first-attempt time,
/// so the attempt for tomorrow's prices starts on time rather than up to an interval late.
/// </summary>
/// <param name="options">The price settings.</param>
internal sealed class PriceImportSchedule(PricesOptions options)
{
    /// <summary>Returns the Berlin day that contains <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>Today.</returns>
    public static DateOnly Today(DateTimeOffset nowUtc) => BerlinTime.DayOf(nowUtc);

    /// <summary>Returns the Berlin day after the one that contains <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>Tomorrow.</returns>
    public static DateOnly Tomorrow(DateTimeOffset nowUtc) => Today(nowUtc).AddDays(1);

    /// <summary>Returns whether a run at <paramref name="nowUtc"/> fetches tomorrow: Berlin time in [first attempt, give-up).</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns><see langword="true"/> inside the window.</returns>
    public bool FetchesTomorrow(DateTimeOffset nowUtc)
    {
        var today = Today(nowUtc);
        return nowUtc >= BerlinTime.AtLocal(today, options.FirstAttemptAt) && nowUtc < GiveUpTime(today);
    }

    /// <summary>Returns whether the give-up time for tomorrow has passed at <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns><see langword="true"/> at or after the give-up time.</returns>
    public bool PastGiveUp(DateTimeOffset nowUtc) => nowUtc >= GiveUpTime(Today(nowUtc));

    /// <summary>Returns when the run after one at <paramref name="nowUtc"/> starts.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The earlier of one retry interval later and the next first-attempt time.</returns>
    public DateTimeOffset Next(DateTimeOffset nowUtc)
    {
        var today = Today(nowUtc);
        var firstAttempt = BerlinTime.AtLocal(today, options.FirstAttemptAt);
        if (firstAttempt <= nowUtc)
        {
            firstAttempt = BerlinTime.AtLocal(today.AddDays(1), options.FirstAttemptAt);
        }

        var retry = nowUtc + options.RetryInterval;
        return retry < firstAttempt ? retry : firstAttempt;
    }

    private DateTimeOffset GiveUpTime(DateOnly today) => BerlinTime.AtLocal(today, options.GiveUpAt);
}
