using System.Diagnostics;
using System.Diagnostics.Metrics;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.UnitTests.Observability;

public sealed class RecorderTests
{
    private static readonly ActivitySource Source = new("CheapHours.RecorderTests");
    private static readonly ActivitySource Foreign = new("Elsewhere.RecorderTests");

    [Fact]
    public void Span_recorder_keeps_stopped_cheaphours_spans_by_trace()
    {
        using var recorder = new SpanRecorder();
        ActivityTraceId traceId;
        using (var parent = Source.StartActivity("parent"))
        {
            traceId = parent!.TraceId;
            using (Source.StartActivity("child"))
            {
            }
        }

        using (Source.StartActivity("other trace", ActivityKind.Internal, parentContext: default(ActivityContext)))
        {
        }

        var trace = recorder.InTrace(traceId);
        Assert.Equal(["parent", "child"], trace.Select(a => a.DisplayName));
        Assert.Equal(trace[0].SpanId, trace[1].ParentSpanId);
    }

    [Fact]
    public void Span_recorder_ignores_other_sources_and_unrecorded_spans()
    {
        using var recorder = new SpanRecorder();
        ActivityTraceId traceId;
        using (var parent = Source.StartActivity("kept"))
        {
            traceId = parent!.TraceId;
            using (Foreign.StartActivity("foreign"))
            {
            }

            using var dropped = Source.StartActivity("dropped");
            dropped!.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }

        Assert.Equal(["kept"], recorder.InTrace(traceId).Select(a => a.DisplayName));
    }

    [Fact]
    public void Span_recorder_finds_a_trace_by_its_traceparent()
    {
        using var recorder = new SpanRecorder();
        string traceparent;
        using (var span = Source.StartActivity("from header"))
        {
            traceparent = span!.Id!;
        }

        Assert.Equal("from header", Assert.Single(recorder.InTrace(traceparent)).DisplayName);
    }

    [Fact]
    public void Metric_recorder_collects_named_instruments_with_their_tags_within_its_scope()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var others = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var recorder = new MetricRecorder(factory, "test.requests", "test.gauge");
        var meter = factory.Create("CheapHours.RecorderTests");
        var counter = meter.CreateCounter<long>("test.requests");
        var ignored = meter.CreateCounter<long>("test.ignored");
        var elsewhere = others.GetRequiredService<IMeterFactory>().Create("CheapHours.RecorderTests").CreateCounter<long>("test.requests");
        int? level = 7;
        meter.CreateObservableGauge("test.gauge", () => level is { } value ? [new Measurement<int>(value, new KeyValuePair<string, object?>("provider.id", "ocpp"))] : Array.Empty<Measurement<int>>());

        counter.Add(2, new KeyValuePair<string, object?>("provider.id", "carcloud"), new KeyValuePair<string, object?>("outcome", "success"));
        counter.Add(3, new KeyValuePair<string, object?>("provider.id", "ocpp"));
        ignored.Add(100);
        elsewhere.Add(100, new KeyValuePair<string, object?>("provider.id", "carcloud"));

        Assert.Equal(2, recorder.Sum("test.requests", ("provider.id", "carcloud")));
        Assert.Equal(5, recorder.Sum("test.requests"));
        Assert.Empty(recorder.Of("test.ignored"));
        Assert.Equal(7, recorder.Latest("test.gauge", ("provider.id", "ocpp")));
        level = 1;
        Assert.Equal(1, recorder.Latest("test.gauge", ("provider.id", "ocpp")));
        level = null;
        Assert.Null(recorder.Latest("test.gauge", ("provider.id", "ocpp")));
    }
}
