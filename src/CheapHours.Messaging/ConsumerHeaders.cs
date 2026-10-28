namespace CheapHours.Messaging;

/// <summary>The headers the consumer adds to retried and dead-lettered messages, and its failure categories.</summary>
public static class ConsumerHeaders
{
    /// <summary>On a retry copy: which retry it is (1, 2 or 3).</summary>
    public const string Attempt = "x-attempt";

    /// <summary>On a dead-lettered message: how many times it was attempted.</summary>
    public const string Attempts = "x-attempts";

    /// <summary>On a dead-lettered message: why, as one of the categories below.</summary>
    public const string FailureCategory = "x-failure-category";

    /// <summary>On a dead-lettered message: when it was parked (ISO 8601 UTC).</summary>
    public const string FailedAt = "x-failed-at";

    /// <summary>The handler failed on every attempt.</summary>
    public const string HandlerFailed = "handler-failed";

    /// <summary>The body could not be read as the expected message type.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The message has a schema version newer than the consumer supports.</summary>
    public const string UnsupportedVersion = "unsupported-version";
}
