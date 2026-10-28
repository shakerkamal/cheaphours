using System.Globalization;
using System.Text;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Messaging;

public sealed class ConsumerRetryTests(RabbitMqFixture rabbit)
{
    private static string Header(BasicGetResult got, string name) =>
        got.BasicProperties.Headers![name] switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };

    private static bool Contains(IReadOnlyList<Delivered<TelemetryReceived>> batch, Guid id) => batch.Any(d => d.Message.MessageId == id);

    [Fact]
    public async Task A_message_that_always_fails_is_attempted_four_times_then_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(ConsumerRetryTests));
        var message = TestMessages.Telemetry(TestMessages.NewDevice());
        var handler = new RecordingHandler { Before = _ => throw new InvalidOperationException("boom") };
        harness.Consume(handler);

        await harness.Publisher.PublishAsync(message, ct);
        var parked = await harness.TakeAsync(harness.Topology.Telemetry.DeadLetter, TimeSpan.FromSeconds(15));

        var attempts = handler.Calls.Where(c => Contains(c.Batch, message.MessageId)).Select(c => c.At).ToList();
        Assert.Equal(4, attempts.Count);
        double[] delays = [200, 300, 400];
        for (var i = 0; i < 3; i++)
        {
            Assert.True((attempts[i + 1] - attempts[i]).TotalMilliseconds >= delays[i] * 0.9, $"Retry {i + 1} came after {(attempts[i + 1] - attempts[i]).TotalMilliseconds} ms.");
        }

        Assert.Equal("4", Header(parked, ConsumerHeaders.Attempts));
        Assert.Equal(ConsumerHeaders.HandlerFailed, Header(parked, ConsumerHeaders.FailureCategory));
        Assert.Equal(message.MessageId.ToString(), parked.BasicProperties.MessageId);
        Assert.DoesNotContain("boom", string.Join(" ", parked.BasicProperties.Headers!.Values.Select(v => v is byte[] b ? Encoding.UTF8.GetString(b) : v?.ToString())), StringComparison.Ordinal);
        Assert.True(handler.Calls.Where(c => Contains(c.Batch, message.MessageId)).Skip(1).All(c => c.Batch.Single().IsRepeat));
    }

    [Fact]
    public async Task A_message_that_fails_once_is_handled_once_and_not_parked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(ConsumerRetryTests));
        var message = TestMessages.Telemetry(TestMessages.NewDevice());
        var failures = 0;
        var handler = new RecordingHandler
        {
            Before = _ =>
            {
                if (Interlocked.Increment(ref failures) == 1)
                {
                    throw new InvalidOperationException("once");
                }
            },
        };
        harness.Consume(handler);

        await harness.Publisher.PublishAsync(message, ct);
        await ConsumerHarness.EventuallyAsync(() => handler.TimesHandled(message.MessageId) == 1, TimeSpan.FromSeconds(10), "the retry to succeed");
        await Task.Delay(1000, ct);

        Assert.Equal(1, handler.TimesHandled(message.MessageId));
        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Telemetry.DeadLetter));
    }

    [Fact]
    public async Task One_bad_message_does_not_hold_back_the_rest_of_its_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(ConsumerRetryTests), o => o.BatchWait = TimeSpan.FromSeconds(1));
        var messages = Enumerable.Range(0, 50).Select(_ => TestMessages.Telemetry(TestMessages.NewDevice())).ToList();
        var bad = messages[17].MessageId;
        foreach (var message in messages)
        {
            await harness.Publisher.PublishAsync(message, ct);
        }

        var handler = new RecordingHandler
        {
            Before = batch =>
            {
                if (Contains(batch, bad))
                {
                    throw new InvalidOperationException("bad one");
                }
            },
        };
        harness.Consume(handler);

        await ConsumerHarness.EventuallyAsync(() => handler.HandledCount == 49, TimeSpan.FromSeconds(10), "the 49 good messages");
        Assert.All(messages.Where(m => m.MessageId != bad), m => Assert.Equal(1, handler.TimesHandled(m.MessageId)));
        Assert.Equal(0, handler.TimesHandled(bad));

        var parked = await harness.TakeAsync(harness.Topology.Telemetry.DeadLetter, TimeSpan.FromSeconds(15));
        Assert.Equal(bad.ToString(), parked.BasicProperties.MessageId);
    }

    [Fact]
    public async Task A_message_never_acknowledged_is_parked_by_the_broker_after_twenty_deliveries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(ConsumerRetryTests));
        var message = TestMessages.Telemetry(TestMessages.NewDevice());
        await harness.Publisher.PublishAsync(message, ct);

        // A consumer that dies before settling: take the message and hand it back, over and over.
        await using var channel = await harness.Connection.Current.CreateChannelAsync(cancellationToken: ct);
        for (var i = 0; i < 40 && await harness.CountAsync(harness.Topology.Telemetry.DeadLetter) == 0; i++)
        {
            if (await channel.BasicGetAsync(harness.Topology.Telemetry.Main, autoAck: false, ct) is { } got)
            {
                await channel.BasicNackAsync(got.DeliveryTag, multiple: false, requeue: true, ct);
            }
            else
            {
                await Task.Delay(50, ct);
            }
        }

        var parked = await harness.TakeAsync(harness.Topology.Telemetry.DeadLetter);
        Assert.Equal(message.MessageId.ToString(), parked.BasicProperties.MessageId);
        var death = Assert.IsType<List<object>>(parked.BasicProperties.Headers!["x-death"]).Cast<Dictionary<string, object>>().First();
        Assert.Equal("delivery_limit", Encoding.UTF8.GetString((byte[])death["reason"]));
    }
}
