using System.Collections.Concurrent;
using System.Diagnostics;

namespace CheapHours.Tests.Observability;

/// <summary>
/// Collects every recorded span that stops while it is alive, from the CheapHours sources, ASP.NET Core and
/// HttpClient: what an exporter would receive. Tests run in parallel in one process, so a test always asks for the
/// spans of its own trace, never for a global count.
/// </summary>
public sealed class SpanRecorder : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public SpanRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => Listens(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // A span the SDK dropped (the health and /ocpp filter) is not recorded and would not be exported.
                if (activity.Recorded)
                {
                    _stopped.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Gets every recorded span so far.</summary>
    public IReadOnlyList<Activity> All => [.. _stopped];

    /// <summary>Returns whether spans of <paramref name="sourceName"/> are collected.</summary>
    public static bool Listens(string sourceName) =>
        sourceName.StartsWith("CheapHours.", StringComparison.Ordinal)
        || sourceName is "Microsoft.AspNetCore" or "System.Net.Http";

    /// <summary>Returns the stopped spans of one trace, oldest first.</summary>
    public IReadOnlyList<Activity> InTrace(ActivityTraceId traceId) => [.. _stopped.Where(a => a.TraceId == traceId).OrderBy(a => a.StartTimeUtc)];

    /// <summary>Returns the stopped spans of the trace a W3C <c>traceparent</c> names.</summary>
    public IReadOnlyList<Activity> InTrace(string traceparent) => InTrace(TraceIdOf(traceparent));

    /// <summary>Returns a new sampled W3C trace context, so parallel tests never share a trace.</summary>
    public static string NewTraceparent() => $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

    /// <summary>Returns the trace id a W3C <c>traceparent</c> holds.</summary>
    public static ActivityTraceId TraceIdOf(string traceparent) =>
        ActivityContext.TryParse(traceparent, null, out var context)
            ? context.TraceId
            : throw new ArgumentException($"Not a W3C traceparent: '{traceparent}'.", nameof(traceparent));

    /// <summary>Waits until a stopped span matches, and returns the first that does.</summary>
    public async Task<Activity> WaitForAsync(Func<Activity, bool> match, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            if (_stopped.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No matching span stopped in time. Spans so far: " + string.Join(", ", _stopped.Select(a => a.DisplayName).Distinct()));
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Waits until the trace holds a span for each name in <paramref name="names"/>; returns the trace.</summary>
    public async Task<IReadOnlyList<Activity>> WaitForTraceAsync(ActivityTraceId traceId, IEnumerable<string> names, TimeSpan? within = null)
    {
        var wanted = names.ToArray();
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var trace = InTrace(traceId);
            var missing = wanted.Where(name => trace.All(a => a.DisplayName != name)).ToArray();
            if (missing.Length == 0)
            {
                return trace;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Trace {traceId} lacks spans [{string.Join(", ", missing)}]; it has [{string.Join(", ", trace.Select(a => a.DisplayName))}].");
            }

            await Task.Delay(50);
        }
    }

    public void Dispose() => _listener.Dispose();
}
