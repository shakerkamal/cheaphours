using CheapHours.Messaging;

namespace CheapHours.UnitTests.Messaging;

public sealed class MessagingTopologyTests
{
    private static readonly MessagingTopology Default = new(new MessagingOptions());

    [Fact]
    public void Names_follow_the_design()
    {
        Assert.Equal("cheaphours.events", Default.EventsExchange);
        Assert.Equal("worker.telemetry", Default.Telemetry.Main);
        Assert.Equal(["worker.telemetry.retry.1", "worker.telemetry.retry.2", "worker.telemetry.retry.3"], Default.Telemetry.Retries);
        Assert.Equal("worker.telemetry.dlq", Default.Telemetry.DeadLetter);
    }

    [Fact]
    public void A_prefix_applies_to_every_name()
    {
        var topology = new MessagingTopology(new MessagingOptions { QueuePrefix = "t1." });

        Assert.Equal("t1.cheaphours.events", topology.EventsExchange);
        Assert.All(topology.Queues, q => Assert.StartsWith("t1.", q.Name, StringComparison.Ordinal));
        Assert.All(topology.Bindings, b => Assert.StartsWith("t1.", b.Exchange, StringComparison.Ordinal));
    }

    [Fact]
    public void Telemetry_is_bound_to_the_events_exchange_by_message_type()
    {
        var binding = Assert.Single(Default.Bindings);

        Assert.Equal(new BindingDefinition("cheaphours.events", "worker.telemetry", "TelemetryReceived"), binding);
    }

    [Fact]
    public void The_main_queue_is_a_quorum_queue_that_parks_after_twenty_deliveries()
    {
        var main = Queue(Default.Telemetry.Main);

        Assert.Equal("quorum", main.Arguments["x-queue-type"]);
        Assert.Equal(20, main.Arguments["x-delivery-limit"]);
        Assert.Equal(string.Empty, main.Arguments["x-dead-letter-exchange"]);
        Assert.Equal("worker.telemetry.dlq", main.Arguments["x-dead-letter-routing-key"]);
        Assert.Equal("at-least-once", main.Arguments["x-dead-letter-strategy"]);
        Assert.Equal("reject-publish", main.Arguments["x-overflow"]);
    }

    [Fact]
    public void Retry_queues_wait_their_delay_then_return_to_the_main_queue()
    {
        Assert.Equal([5_000, 30_000, 120_000], Default.Telemetry.Retries.Select(r => Queue(r).Arguments["x-message-ttl"]));
        Assert.All(Default.Telemetry.Retries, r =>
        {
            var retry = Queue(r);
            Assert.Equal("quorum", retry.Arguments["x-queue-type"]);
            Assert.Equal(string.Empty, retry.Arguments["x-dead-letter-exchange"]);
            Assert.Equal("worker.telemetry", retry.Arguments["x-dead-letter-routing-key"]);
            Assert.Equal("at-least-once", retry.Arguments["x-dead-letter-strategy"]);
            Assert.Equal("reject-publish", retry.Arguments["x-overflow"]);
        });
    }

    [Fact]
    public void Retry_delays_come_from_the_options()
    {
        var topology = new MessagingTopology(new MessagingOptions
        {
            FirstRetryDelay = TimeSpan.FromMilliseconds(200),
            SecondRetryDelay = TimeSpan.FromMilliseconds(300),
            ThirdRetryDelay = TimeSpan.FromMilliseconds(400),
        });

        Assert.Equal([200, 300, 400], topology.Telemetry.Retries.Select(r => topology.Queues.Single(q => q.Name == r).Arguments["x-message-ttl"]));
    }

    [Fact]
    public void The_dead_letter_queue_is_a_plain_quorum_queue()
    {
        var dlq = Queue(Default.Telemetry.DeadLetter);

        Assert.Equal(new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, dlq.Arguments);
    }

    private static QueueDefinition Queue(string name) => Default.Queues.Single(q => q.Name == name);
}
