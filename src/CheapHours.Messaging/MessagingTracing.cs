using System.Diagnostics;
using CheapHours.Messaging.Contracts;

namespace CheapHours.Messaging;

/// <summary>
/// Spans for publishing and handling messages, on the <c>CheapHours.Messaging</c> source. A message keeps the trace
/// context it was created with at every hop; publish and handling spans are children of that context, and the
/// message's <c>traceparent</c> is never rewritten (ADR-009). A message without one gets a new trace.
/// </summary>
public static class MessagingTracing
{
    /// <summary>The activity source's name.</summary>
    public const string SourceName = "CheapHours.Messaging";

    /// <summary>The activity source.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts the producer span of one publish.</summary>
    /// <param name="destination">The exchange, or the queue for a publish through the default exchange.</param>
    /// <param name="routingKey">The routing key.</param>
    /// <param name="messageId">The message id.</param>
    /// <param name="messageType">The message type.</param>
    /// <param name="traceparent">The message's trace context, if any.</param>
    /// <returns>The scope; dispose it when the broker has confirmed or refused the message.</returns>
    public static SpanScope StartPublish(string destination, string routingKey, Guid messageId, string messageType, string? traceparent)
    {
        var scope = Start($"{destination} publish", ActivityKind.Producer, "publish", destination, messageId, messageType, traceparent);
        return scope.Tag("messaging.rabbitmq.destination.routing_key", routingKey);
    }

    /// <summary>Starts the consumer span of handling one message.</summary>
    /// <param name="queue">The queue it was delivered from.</param>
    /// <param name="message">The message.</param>
    /// <returns>The scope; dispose it when the message is handled.</returns>
    public static SpanScope StartProcess(string queue, Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Start($"{queue} process", ActivityKind.Consumer, "process", queue, message.MessageId, message.MessageType, message.Traceparent);
    }

    private static SpanScope Start(string name, ActivityKind kind, string operation, string destination, Guid messageId, string messageType, string? traceparent) =>
        SpanScope.Start(Source, name, kind, traceparent)
            .Tag("messaging.system", "rabbitmq")
            .Tag("messaging.operation.type", operation)
            .Tag("messaging.destination.name", destination)
            .Tag("messaging.message.id", messageId.ToString())
            .Tag("cheaphours.message.type", messageType);
}
