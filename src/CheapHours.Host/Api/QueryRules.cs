using CheapHours.Core;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api;

/// <summary>
/// Collects query-parameter errors keyed by parameter name, so one 400 response lists every problem.
/// Handlers check their query first and look anything up only when <see cref="IsValid"/>.
/// </summary>
internal sealed class QueryRules
{
    /// <summary>The page size when the client sends none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page a client may ask for.</summary>
    public const int MaxLimit = 200;

    /// <summary>The only telemetry resolution offered.</summary>
    public const string FifteenMinutes = "15m";

    /// <summary>The longest time range a client may ask for.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(31);

    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether every checked parameter was valid.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>Gets the messages per invalid parameter.</summary>
    public IDictionary<string, string[]> Errors => _errors;

    /// <summary>Checks <c>limit</c>.</summary>
    /// <param name="limit">The value sent, if any.</param>
    /// <returns>The page size to use.</returns>
    public int Limit(int? limit)
    {
        if (limit is null)
        {
            return DefaultLimit;
        }

        if (limit is < 1 or > MaxLimit)
        {
            Add("limit", $"Must be between 1 and {MaxLimit}.");
        }

        return limit.Value;
    }

    /// <summary>Checks <c>from</c> and <c>to</c>: both UTC, <c>from</c> before <c>to</c>, at most 31 days apart.</summary>
    /// <param name="from">The start of the range.</param>
    /// <param name="to">The end of the range.</param>
    public void Range(DateTimeOffset from, DateTimeOffset to)
    {
        var utc = true;
        if (from.Offset != TimeSpan.Zero)
        {
            Add("from", "Must be a UTC time ending in 'Z'.");
            utc = false;
        }

        if (to.Offset != TimeSpan.Zero)
        {
            Add("to", "Must be a UTC time ending in 'Z'.");
            utc = false;
        }

        if (!utc)
        {
            return;
        }

        if (from >= to)
        {
            Add("to", "Must be after 'from'.");
        }
        else if (to - from > MaxSpan)
        {
            Add("to", $"The range must not be longer than {MaxSpan.TotalDays:0} days.");
        }
    }

    /// <summary>Checks <c>resolution</c>: absent or <c>15m</c>.</summary>
    /// <param name="resolution">The value sent, if any.</param>
    public void Resolution(string? resolution)
    {
        if (resolution is not null && !string.Equals(resolution, FifteenMinutes, StringComparison.Ordinal))
        {
            Add("resolution", $"Only '{FifteenMinutes}' is supported.");
        }
    }

    /// <summary>Checks a device-list cursor.</summary>
    /// <param name="cursor">The value sent, if any.</param>
    /// <returns>The device to continue after, or <see langword="null"/> to start at the beginning.</returns>
    public DeviceId? DeviceCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (PageCursor.TryReadDevice(cursor, out var after))
        {
            return after;
        }

        Add("cursor", "Not a cursor this endpoint issued.");
        return null;
    }

    /// <summary>Checks a telemetry cursor against the widened range.</summary>
    /// <param name="cursor">The value sent, if any.</param>
    /// <param name="start">The widened range start (on the grid).</param>
    /// <param name="end">The widened range end (on the grid).</param>
    /// <returns>The first slot of the page: <paramref name="start"/>, or the slot after the cursor's bucket.</returns>
    public DateTimeOffset TelemetryCursor(string? cursor, DateTimeOffset start, DateTimeOffset end)
    {
        if (cursor is null)
        {
            return start;
        }

        if (PageCursor.TryReadBucket(cursor, out var lastStart) && lastStart >= start && lastStart < end)
        {
            return lastStart + SlotGrid.SlotLength;
        }

        Add("cursor", "Not a cursor this endpoint issued for this range.");
        return start;
    }

    private void Add(string parameter, string message) =>
        _errors[parameter] = _errors.TryGetValue(parameter, out var existing) ? [.. existing, message] : [message];
}
