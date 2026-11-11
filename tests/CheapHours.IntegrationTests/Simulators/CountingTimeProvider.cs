namespace CheapHours.IntegrationTests.Simulators;

/// <summary>A clock that counts the timers created on it and otherwise passes everything to another clock.</summary>
/// <param name="inner">The clock that keeps the time and runs the timers.</param>
internal sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider
{
    private int _timersCreated;

    /// <summary>Gets how many timers have been created so far.</summary>
    public int TimersCreated => Volatile.Read(ref _timersCreated);

    /// <inheritdoc/>
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    /// <inheritdoc/>
    public override long TimestampFrequency => inner.TimestampFrequency;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    /// <inheritdoc/>
    public override long GetTimestamp() => inner.GetTimestamp();

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timersCreated);
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}
