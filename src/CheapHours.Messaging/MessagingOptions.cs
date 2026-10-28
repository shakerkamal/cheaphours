namespace CheapHours.Messaging;

/// <summary>RabbitMQ settings shared by publishers and consumers (section <c>Messaging</c>).</summary>
public sealed class MessagingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Messaging";

    /// <summary>The connection string name (<c>ConnectionStrings:RabbitMq</c>); it holds a password, so it is never logged.</summary>
    public const string ConnectionStringName = "RabbitMq";

    /// <summary>Gets or sets a prefix for every exchange and queue name; empty in production, unique per test class.</summary>
    public string QueuePrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the wait before the second attempt.</summary>
    public TimeSpan FirstRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the wait before the third attempt.</summary>
    public TimeSpan SecondRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the wait before the fourth and last attempt.</summary>
    public TimeSpan ThirdRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the most messages handled in one batch.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Gets or sets how long a batch waits for more messages after the first one arrives.</summary>
    public TimeSpan BatchWait { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets or sets how many unacknowledged messages the broker hands one consumer.</summary>
    public int Prefetch { get; set; } = 400;

    /// <summary>Gets or sets how long a publish waits for the broker's confirmation.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the three retry delays in order; three retries means four attempts in all.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays => [FirstRetryDelay, SecondRetryDelay, ThirdRetryDelay];
}
