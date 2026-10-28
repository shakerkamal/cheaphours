using CheapHours.Messaging.Contracts;

namespace CheapHours.Messaging;

/// <summary>Handles a batch of delivered messages for a <see cref="BatchingConsumer{TMessage}"/>.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
public interface IBatchHandler<TMessage>
    where TMessage : Message
{
    /// <summary>Handles the batch; it must be safe to call again with the same messages.</summary>
    /// <param name="batch">The messages, in delivery order.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>A task that completes when every message is handled.</returns>
    /// <exception cref="DependencyUnavailableException">A dependency is down; the batch is kept and tried again later.</exception>
    Task HandleAsync(IReadOnlyList<Delivered<TMessage>> batch, CancellationToken cancellationToken);
}
