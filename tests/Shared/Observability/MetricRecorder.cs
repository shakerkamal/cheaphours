using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CheapHours.Tests.Observability;

/// <summary>One measurement: the instrument, the value and its tags.</summary>
public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags)
{
    /// <summary>Returns whether every given tag has the given value.</summary>
    public bool Has(params (string Key, string? Value)[] tags) =>
        tags.All(t => Tags.TryGetValue(t.Key, out var value) && value == t.Value);
}

/// <summary>
/// Collects measurements of the named instruments while it is alive. Hosts in one test process share meter names,
/// so a recorder can be limited to one host's meters (<paramref name="scope"/>: that host's <c>IMeterFactory</c>).
/// Observable instruments report only when <see cref="Collect"/> is called.
/// </summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly MeterListener _listener;

    /// <param name="scope">Only meters created with this scope (an <c>IMeterFactory</c>); any meter when null.</param>
    /// <param name="instruments">The instrument names to collect.</param>
    public MetricRecorder(object? scope, params string[] instruments)
    {
        var names = instruments.ToHashSet(StringComparer.Ordinal);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (names.Contains(instrument.Name) && (scope is null || ReferenceEquals(instrument.Meter.Scope, scope)))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Gets every measurement so far, oldest first.</summary>
    public IReadOnlyList<Measurement> All => [.. _measurements];

    /// <summary>Returns the measurements of one instrument whose tags match.</summary>
    public IReadOnlyList<Measurement> Of(string instrument, params (string Key, string? Value)[] tags) =>
        [.. _measurements.Where(m => m.Instrument == instrument && m.Has(tags))];

    /// <summary>Returns the sum of one counter's measurements whose tags match.</summary>
    public double Sum(string instrument, params (string Key, string? Value)[] tags) => Of(instrument, tags).Sum(m => m.Value);

    /// <summary>
    /// Waits until a measurement of one instrument with matching tags arrives (a metric is recorded just after the
    /// stored change a test can see), and returns all of them.
    /// </summary>
    public async Task<IReadOnlyList<Measurement>> WaitForAsync(string instrument, params (string Key, string? Value)[] tags)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Of(instrument, tags) is { Count: 0 } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return Of(instrument, tags);
    }

    /// <summary>Asks every observable instrument for its current values.</summary>
    public void Collect() => _listener.RecordObservableInstruments();

    /// <summary>
    /// Collects the observable instruments and returns the value this collection reported for one whose tags match,
    /// or <see langword="null"/> when it reported none (a gauge that has stopped reporting is not stale-read).
    /// </summary>
    public double? Latest(string instrument, params (string Key, string? Value)[] tags)
    {
        var before = _measurements.Count;
        Collect();
        var reported = _measurements.Skip(before).Where(m => m.Instrument == instrument && m.Has(tags)).ToList();
        return reported.Count > 0 ? reported[^1].Value : null;
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, tag) in tags)
        {
            copy[key] = tag?.ToString();
        }

        _measurements.Enqueue(new Measurement(instrument.Name, value, copy));
    }
}
