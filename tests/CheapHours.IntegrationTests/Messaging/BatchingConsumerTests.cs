using System.Globalization;
using System.Text;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;

namespace CheapHours.IntegrationTests.Messaging;

public sealed class BatchingConsumerTests(RabbitMqFixture rabbit)
{
    private static string Header(RabbitMQ.Client.BasicGetResult got, string name) =>
        got.BasicProperties.Headers![name] switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };

    [Fact]
    public async Task A_lone_message_is_handled_within_a_second()
    {
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(BatchingConsumerTests));
        var handler = new RecordingHandler();
        harness.Consume(handler);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var message = TestMessages.Telemetry(TestMessages.NewDevice());

        var published = DateTime.UtcNow;
        await harness.Publisher.PublishAsync(message, TestContext.Current.CancellationToken);
        await ConsumerHarness.EventuallyAsync(() => handler.TimesHandled(message.MessageId) == 1, TimeSpan.FromSeconds(5), "the message to be handled");

        Assert.True(handler.Calls.Single().At - published < TimeSpan.FromSeconds(1));
        Assert.False(handler.Calls.Single().Batch.Single().IsRepeat);
        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Telemetry.Main));
    }

    [Fact]
    public async Task Messages_arriving_together_are_handled_in_batches_of_at_most_the_batch_size()
    {
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(BatchingConsumerTests), o => o.BatchSize = 50);
        var messages = Enumerable.Range(0, 120).Select(_ => TestMessages.Telemetry(TestMessages.NewDevice())).ToList();
        foreach (var message in messages)
        {
            await harness.Publisher.PublishAsync(message, TestContext.Current.CancellationToken);
        }

        var handler = new RecordingHandler();
        harness.Consume(handler);
        await ConsumerHarness.EventuallyAsync(() => handler.HandledCount == messages.Count, TimeSpan.FromSeconds(10), "every message to be handled");

        Assert.All(messages, m => Assert.Equal(1, handler.TimesHandled(m.MessageId)));
        Assert.All(handler.Calls, c => Assert.True(c.Batch.Count <= 50));
        Assert.True(handler.Calls.Count < messages.Count);
    }

    [Fact]
    public async Task An_unreadable_body_is_dead_lettered_at_once()
    {
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(BatchingConsumerTests));
        var handler = new RecordingHandler();
        harness.Consume(handler);
        var id = Guid.CreateVersion7().ToString();

        await harness.PublishRawAsync(Encoding.UTF8.GetBytes("{{{"), id);

        var parked = await harness.TakeAsync(harness.Topology.Telemetry.DeadLetter);
        Assert.Equal("{{{", Encoding.UTF8.GetString(parked.Body.Span));
        Assert.Equal(id, parked.BasicProperties.MessageId);
        Assert.Equal(ConsumerHeaders.Unreadable, Header(parked, ConsumerHeaders.FailureCategory));
        Assert.Equal("1", Header(parked, ConsumerHeaders.Attempts));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_future_schema_version_is_dead_lettered_at_once()
    {
        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(BatchingConsumerTests));
        var handler = new RecordingHandler();
        harness.Consume(handler);

        await harness.PublishWithVersionAsync(TestMessages.Telemetry(TestMessages.NewDevice()), schemaVersion: 2);

        var parked = await harness.TakeAsync(harness.Topology.Telemetry.DeadLetter);
        Assert.Equal(ConsumerHeaders.UnsupportedVersion, Header(parked, ConsumerHeaders.FailureCategory));
        Assert.Empty(handler.Calls);
    }
}
