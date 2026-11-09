using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Messaging;

/// <summary>
/// distributed-tracing: "Message spans continue the trace the message carries". Publish and handling spans are
/// children of the message's context, and the message keeps its <c>traceparent</c> unchanged in header and body.
/// </summary>
public sealed class MessagingTracingTests(RabbitMqFixture rabbit)
{
    private static readonly ActivitySource Ambient = new("CheapHours.MessagingTracingTests");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A trace context of this test's own, so parallel tests never share a trace.</summary>
    private static string NewTraceparent() => $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

    [Fact]
    public async Task A_message_with_a_trace_context_is_published_under_it_and_keeps_it()
    {
        using var spans = new SpanRecorder();
        var traceparent = NewTraceparent();
        var (connection, publisher, topology) = await StartAsync();
        await using var _ = connection;
        await using var __ = publisher;
        var message = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 1, traceparent: traceparent);
        var parent = ActivityContext.Parse(traceparent, null);

        using (Ambient.StartActivity("caller"))
        {
            await publisher.PublishAsync(message, Ct);
        }

        var got = await GetAsync(connection.Current, topology.Telemetry.Main);
        Assert.Equal(traceparent, Encoding.UTF8.GetString((byte[])got.BasicProperties.Headers![RabbitMqPublisher.TraceparentHeader]!));
        using var body = JsonDocument.Parse(got.Body);
        Assert.Equal(traceparent, body.RootElement.GetProperty("traceparent").GetString());

        var publish = Assert.Single(spans.InTrace(parent.TraceId));
        Assert.Equal(ActivityKind.Producer, publish.Kind);
        Assert.Equal(parent.SpanId, publish.ParentSpanId);
        Assert.Equal($"{topology.EventsExchange} publish", publish.DisplayName);
        Assert.Equal("rabbitmq", publish.GetTagItem("messaging.system"));
        Assert.Equal(topology.EventsExchange, publish.GetTagItem("messaging.destination.name"));
        Assert.Equal(message.MessageId.ToString(), publish.GetTagItem("messaging.message.id"));
        Assert.Equal(message.MessageType, publish.GetTagItem("cheaphours.message.type"));
    }

    [Fact]
    public async Task A_message_without_a_trace_context_is_published_in_a_new_trace_and_delivered()
    {
        using var spans = new SpanRecorder();
        var (connection, publisher, topology) = await StartAsync();
        await using var _ = connection;
        await using var __ = publisher;
        var message = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 1);
        ActivityTraceId callerTrace;

        using (var caller = Ambient.StartActivity("caller"))
        {
            callerTrace = caller!.TraceId;
            await publisher.SendToQueueAsync(message, topology.Telemetry.Main, Ct);

            Assert.Same(caller, Activity.Current);
        }

        var got = await GetAsync(connection.Current, topology.Telemetry.Main);
        Assert.False(got.BasicProperties.Headers!.ContainsKey(RabbitMqPublisher.TraceparentHeader));
        var publish = await spans.WaitForAsync(a => Equals(a.GetTagItem("messaging.message.id"), message.MessageId.ToString()));
        Assert.Equal(default, publish.ParentSpanId);
        Assert.NotEqual(callerTrace, publish.TraceId);
        Assert.Equal($"{topology.Telemetry.Main} publish", publish.DisplayName);
    }

    [Fact]
    public void Handling_a_message_is_a_consumer_span_under_its_context_or_a_new_root()
    {
        using var spans = new SpanRecorder();
        var traceparent = NewTraceparent();
        var traced = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 1, traceparent: traceparent);
        var untraced = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 1);
        var parent = ActivityContext.Parse(traceparent, null);

        using (var caller = Ambient.StartActivity("consumer loop"))
        {
            using (var scope = MessagingTracing.StartProcess("worker.telemetry", traced))
            {
                Assert.Equal(parent.TraceId, scope.Span!.TraceId);
                Assert.Equal(parent.SpanId, scope.Span.ParentSpanId);
                Assert.Equal(ActivityKind.Consumer, scope.Span.Kind);
                Assert.Equal("worker.telemetry process", scope.Span.DisplayName);
            }

            using (var scope = MessagingTracing.StartProcess("worker.telemetry", untraced))
            {
                Assert.Equal(default, scope.Span!.ParentSpanId);
                Assert.NotEqual(caller!.TraceId, scope.Span.TraceId);
            }

            Assert.Same(caller, Activity.Current);
        }
    }

    [Fact]
    public void A_failed_span_records_the_exception_type_and_not_its_message()
    {
        using var spans = new SpanRecorder();
        var traceparent = NewTraceparent();
        var message = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 1, traceparent: traceparent);

        using (var scope = MessagingTracing.StartProcess("worker.telemetry", message))
        {
            scope.Fail(new InvalidOperationException("vendor said: token abc123 expired"));
        }

        var span = Assert.Single(spans.InTrace(traceparent));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("exception.type"));
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("abc123", StringComparison.Ordinal) == true);
        Assert.Empty(span.Events);
    }

    private async Task<(RabbitMqConnection Connection, RabbitMqPublisher Publisher, MessagingTopology Topology)> StartAsync()
    {
        var options = new MessagingOptions { QueuePrefix = RabbitMqFixture.NewPrefix(nameof(MessagingTracingTests)) };
        var topology = new MessagingTopology(options);
        var connection = new RabbitMqConnection(rabbit.ConnectionString, "tracing-tests", NullLogger<RabbitMqConnection>.Instance);
        await connection.ConnectAsync(Ct);
        await TopologyDeclarer.DeclareAsync(connection.Current, topology, Ct);
        return (connection, new RabbitMqPublisher(connection, topology, Options.Create(options)), topology);
    }

    private static async Task<BasicGetResult> GetAsync(IConnection connection, string queue)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        for (var i = 0; i < 50; i++)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, Ct) is { } got)
            {
                return got;
            }

            await Task.Delay(100, Ct);
        }

        throw new Xunit.Sdk.XunitException($"No message arrived in '{queue}'.");
    }
}
