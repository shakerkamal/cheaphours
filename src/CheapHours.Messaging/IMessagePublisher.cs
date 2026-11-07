using CheapHours.Messaging.Contracts;

namespace CheapHours.Messaging;

/// <summary>Publishes messages; a publish completes only once the broker has confirmed it.</summary>
public interface IMessagePublisher
{
    /// <summary>Publishes <paramref name="message"/> to the events exchange with its message type as routing key.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the broker has confirmed the message.</returns>
    /// <exception cref="BrokerUnavailableException">The broker is unreachable, refused the message, or did not confirm it in time.</exception>
    Task PublishAsync(Message message, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes <paramref name="message"/> through the default exchange straight to <paramref name="queue"/>,
    /// persistent and <c>mandatory</c>: a queue that does not exist makes the publish fail instead of dropping it.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="queue">The queue name, prefix included.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the broker has confirmed the message.</returns>
    /// <exception cref="BrokerUnavailableException">The broker is unreachable, could not route the message, refused it, or did not confirm it in time.</exception>
    Task SendToQueueAsync(Message message, string queue, CancellationToken cancellationToken);

    /// <summary>Publishes a message saved earlier (an outbox entry) with its original envelope.</summary>
    /// <param name="message">The saved message.</param>
    /// <param name="destination">The routing key on the events exchange, or the queue name when <paramref name="toQueue"/> is set.</param>
    /// <param name="toQueue">Whether to publish through the default exchange to the queue <paramref name="destination"/> names.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the broker has confirmed the message.</returns>
    /// <exception cref="BrokerUnavailableException">The broker is unreachable, could not route the message, refused it, or did not confirm it in time.</exception>
    Task PublishSerializedAsync(SerializedMessage message, string destination, bool toQueue, CancellationToken cancellationToken);
}
