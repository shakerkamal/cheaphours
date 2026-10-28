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
}
