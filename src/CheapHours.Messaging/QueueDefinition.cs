namespace CheapHours.Messaging;

/// <summary>A durable queue to declare.</summary>
/// <param name="Name">The queue name, prefix included.</param>
/// <param name="Arguments">The <c>x-</c> arguments; declaring an existing queue with different ones fails.</param>
public sealed record QueueDefinition(string Name, IReadOnlyDictionary<string, object?> Arguments);
