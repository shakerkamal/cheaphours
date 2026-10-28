namespace CheapHours.Messaging;

/// <summary>A binding from an exchange to a queue.</summary>
/// <param name="Exchange">The exchange name.</param>
/// <param name="Queue">The queue name.</param>
/// <param name="RoutingKey">The routing key; the message type for <c>cheaphours.events</c>.</param>
public sealed record BindingDefinition(string Exchange, string Queue, string RoutingKey);
