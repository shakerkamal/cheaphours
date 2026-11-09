using System.Diagnostics;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>Storing a telemetry batch is one span linked to its messages' traces (distributed-tracing: "Batch of two traces").</summary>
public sealed class TelemetryTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_batch_of_two_traced_messages_is_stored_in_one_root_span_with_a_link_to_each()
    {
        using var spans = new SpanRecorder();
        var worker = await WorkerHost.StartAsync(mongo, rabbit, MongoFixture.NewDatabaseName(nameof(TelemetryTracingTests)), RabbitMqFixture.NewPrefix(nameof(TelemetryTracingTests)), new Dictionary<string, string>());
        try
        {
            var first = ActivityContext.Parse(SpanRecorder.NewTraceparent(), null);
            var second = ActivityContext.Parse(SpanRecorder.NewTraceparent(), null);
            var now = DateTimeOffset.UtcNow;
            Delivered<TelemetryReceived> Message(ActivityContext context, string externalId, int readings) => new(
                new TelemetryReceived(
                    Guid.CreateVersion7(),
                    now,
                    $"00-{context.TraceId}-{context.SpanId}-01",
                    DeviceId.New(TimeProvider.System),
                    "ocpp",
                    [.. Enumerable.Range(0, readings).Select(i => new DeviceReading(externalId, now.AddSeconds(i), 7400, 1000 + i))]),
                IsRepeat: false);

            await worker.Services.GetRequiredService<TelemetryBatchHandler>().HandleAsync([Message(first, "CP001", 2), Message(second, "CP002", 1)], Ct);

            var store = await spans.WaitForAsync(a => a.DisplayName == "telemetry store" && a.Links.Any(l => l.Context.TraceId == first.TraceId));
            Assert.Equal(default, store.ParentSpanId);
            Assert.NotEqual(first.TraceId, store.TraceId);
            Assert.Equal(
                [(first.TraceId, first.SpanId), (second.TraceId, second.SpanId)],
                store.Links.Select(l => (l.Context.TraceId, l.Context.SpanId)).OrderBy(l => l.TraceId == second.TraceId));
            Assert.Equal(3, store.GetTagItem("cheaphours.reading.count"));
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
        }
    }
}
