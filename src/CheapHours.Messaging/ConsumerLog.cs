using Microsoft.Extensions.Logging;

namespace CheapHours.Messaging;

/// <summary>The consumer's log messages: queue names, ids, counts and exception type names only, never payloads.</summary>
internal static partial class ConsumerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue}")]
    public static partial void Started(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped consuming {Queue}")]
    public static partial void Stopped(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch of {Count} from {Queue} failed ({Error}); handling each message alone")]
    public static partial void BatchFailed(ILogger logger, string queue, int count, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} from {Queue} failed on attempt {Attempt} ({Error})")]
    public static partial void MessageFailed(ILogger logger, string queue, Guid messageId, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dependency is down; keeping {Count} message(s) from {Queue} and trying again in {Delay}")]
    public static partial void DependencyDown(ILogger logger, string queue, int count, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} from {Queue} was dead-lettered ({Category}, {Attempts} attempt(s))")]
    public static partial void DeadLettered(ILogger logger, string queue, string messageId, string category, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not settle {Count} message(s) from {Queue}: the channel closed; the broker will redeliver them")]
    public static partial void AckLost(ILogger logger, string queue, int count);
}
