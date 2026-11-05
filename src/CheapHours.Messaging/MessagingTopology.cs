using CheapHours.Messaging.Contracts;

namespace CheapHours.Messaging;

/// <summary>
/// The exchanges, queues and bindings the application declares (ADR-003). Messages are published to one
/// topic exchange with their message type as routing key; each consumer has a main quorum queue, three
/// delay queues that dead-letter back into it, and a dead-letter queue. Every name carries the configured
/// prefix.
/// </summary>
public sealed class MessagingTopology
{
    private const string QuorumQueue = "quorum";

    // RabbitMQ 4 drops a quorum-queue message after this many unacknowledged deliveries unless a
    // dead-letter target is set; we set one, so a message that keeps crashing its consumer is parked.
    private const int DeliveryLimit = 20;

    /// <summary>Creates the description from the options.</summary>
    /// <param name="options">The messaging options (prefix and retry delays).</param>
    public MessagingTopology(MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var prefix = options.QueuePrefix;

        EventsExchange = prefix + "cheaphours.events";
        Telemetry = new ConsumerQueues(prefix + "worker.telemetry");
        Planner = new ConsumerQueues(prefix + "worker.planner");

        var queues = new List<QueueDefinition>();
        AddConsumer(queues, Telemetry, options);
        AddConsumer(queues, Planner, options);
        Queues = queues;
        Bindings =
        [
            new BindingDefinition(EventsExchange, Telemetry.Main, TelemetryReceived.TypeName),
            new BindingDefinition(EventsExchange, Planner.Main, PricesPublished.TypeName),
        ];
    }

    /// <summary>Gets the topic exchange every message is published to.</summary>
    public string EventsExchange { get; }

    /// <summary>Gets the telemetry consumer's queues.</summary>
    public ConsumerQueues Telemetry { get; }

    /// <summary>Gets the planner consumer's queues (re-planning on <see cref="PricesPublished"/>).</summary>
    public ConsumerQueues Planner { get; }

    /// <summary>Gets every queue to declare, with its arguments.</summary>
    public IReadOnlyList<QueueDefinition> Queues { get; }

    /// <summary>Gets every binding to declare.</summary>
    public IReadOnlyList<BindingDefinition> Bindings { get; }

    // One consumer's main queue, its delay queues (each dead-letters back into the main queue after its delay)
    // and its dead-letter queue, all with the same settings for every consumer.
    private static void AddConsumer(List<QueueDefinition> queues, ConsumerQueues consumer, MessagingOptions options)
    {
        queues.Add(new QueueDefinition(consumer.Main, new Dictionary<string, object?>
        {
            ["x-queue-type"] = QuorumQueue,
            ["x-delivery-limit"] = DeliveryLimit,
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = consumer.DeadLetter,
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
        }));

        for (var i = 0; i < consumer.Retries.Count; i++)
        {
            queues.Add(new QueueDefinition(consumer.Retries[i], new Dictionary<string, object?>
            {
                ["x-queue-type"] = QuorumQueue,
                ["x-message-ttl"] = (int)options.RetryDelays[i].TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = consumer.Main,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
            }));
        }

        queues.Add(new QueueDefinition(consumer.DeadLetter, new Dictionary<string, object?> { ["x-queue-type"] = QuorumQueue }));
    }
}
