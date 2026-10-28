using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CheapHours.Messaging;

/// <summary>
/// Declares a <see cref="MessagingTopology"/> on the broker. Declaring an identical topology again changes
/// nothing; a queue or exchange that exists with other arguments stops the caller with an error naming it.
/// </summary>
public static class TopologyDeclarer
{
    private const ushort PreconditionFailed = 406;

    /// <summary>Declares every exchange, queue and binding.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="topology">What to declare.</param>
    /// <param name="cancellationToken">Cancels the declaration.</param>
    /// <returns>A task that completes when everything is declared.</returns>
    /// <exception cref="InvalidOperationException">An existing exchange or queue has different arguments.</exception>
    public static async Task DeclareAsync(IConnection connection, MessagingTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(topology);

        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var current = $"Exchange '{topology.EventsExchange}'";
        try
        {
            await channel.ExchangeDeclareAsync(topology.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var queue in topology.Queues)
            {
                current = $"Queue '{queue.Name}'";
                await channel.QueueDeclareAsync(
                    queue.Name,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: queue.Arguments.ToDictionary(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            foreach (var binding in topology.Bindings)
            {
                current = $"Binding of '{binding.Queue}' to '{binding.Exchange}'";
                await channel.QueueBindAsync(binding.Queue, binding.Exchange, binding.RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == PreconditionFailed)
        {
            throw new InvalidOperationException(
                $"{current} already exists with different arguments than this role declares. Delete it or align its arguments, then start again.",
                ex);
        }
    }
}
