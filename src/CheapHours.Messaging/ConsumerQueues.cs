namespace CheapHours.Messaging;

/// <summary>The queues of one consumer: its main queue, one delay queue per retry, and its dead-letter queue.</summary>
public sealed record ConsumerQueues
{
    /// <summary>Creates the names from the main queue's name.</summary>
    /// <param name="main">The main queue's name, prefix included.</param>
    public ConsumerQueues(string main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(main);
        Main = main;
        Retries = [$"{main}.retry.1", $"{main}.retry.2", $"{main}.retry.3"];
        DeadLetter = $"{main}.dlq";
    }

    /// <summary>Gets the queue the consumer reads.</summary>
    public string Main { get; }

    /// <summary>Gets the delay queues for the second, third and fourth attempt.</summary>
    public IReadOnlyList<string> Retries { get; }

    /// <summary>Gets the queue where messages that cannot be handled are parked.</summary>
    public string DeadLetter { get; }
}
