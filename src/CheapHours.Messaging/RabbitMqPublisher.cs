using System.Text;
using System.Text.Json;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CheapHours.Messaging;

/// <summary>
/// Publishes over one confirming channel: each publish is persistent and <c>mandatory</c>, and completes
/// only when the broker confirms it, so a message that is refused, cannot be routed or is not confirmed
/// within the timeout surfaces as <see cref="BrokerUnavailableException"/> instead of vanishing.
/// Publishes are serialised; at the expected ~33 messages a second one channel is plenty (measure first).
/// </summary>
public sealed class RabbitMqPublisher : IMessagePublisher, IAsyncDisposable
{
    /// <summary>The header holding the message's schema version.</summary>
    public const string SchemaVersionHeader = "schemaVersion";

    /// <summary>The header holding the W3C trace parent.</summary>
    public const string TraceparentHeader = "traceparent";

    private const string JsonContentType = "application/json";

    private readonly RabbitMqConnection _connection;
    private readonly MessagingTopology _topology;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    /// <summary>Creates the publisher.</summary>
    /// <param name="connection">The process's broker connection.</param>
    /// <param name="topology">The declared topology.</param>
    /// <param name="options">The messaging options (publish timeout).</param>
    public RabbitMqPublisher(RabbitMqConnection connection, MessagingTopology topology, IOptions<MessagingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(options);
        _connection = connection;
        _topology = topology;
        _timeout = options.Value.PublishTimeout;
    }

    /// <summary>The AMQP properties a message travels with: its envelope, persistent delivery and JSON content.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The properties.</returns>
    public static BasicProperties PropertiesFor(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Properties(message.MessageId, message.MessageType, message.SchemaVersion, message.OccurredAt, message.Traceparent);
    }

    /// <summary>The AMQP properties a saved message travels with; the same as when it was built.</summary>
    /// <param name="message">The saved message.</param>
    /// <returns>The properties.</returns>
    public static BasicProperties PropertiesFor(SerializedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Properties(message.MessageId, message.Type, message.SchemaVersion, message.OccurredAt, message.Traceparent);
    }

    /// <inheritdoc/>
    public Task PublishAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), CheapHoursJson.Options);
        return TracedPublishAsync(_topology.EventsExchange, message.MessageType, body, PropertiesFor(message), message.MessageId, message.MessageType, message.Traceparent, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SendToQueueAsync(Message message, string queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), CheapHoursJson.Options);
        return TracedPublishAsync(string.Empty, queue, body, PropertiesFor(message), message.MessageId, message.MessageType, message.Traceparent, cancellationToken);
    }

    /// <inheritdoc/>
    public Task PublishSerializedAsync(SerializedMessage message, string destination, bool toQueue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return TracedPublishAsync(
            toQueue ? string.Empty : _topology.EventsExchange,
            destination,
            Encoding.UTF8.GetBytes(message.Json),
            PropertiesFor(message),
            message.MessageId,
            message.Type,
            message.Traceparent,
            cancellationToken);
    }

    /// <summary>Publishes a body as is, for example a copy sent to a retry or dead-letter queue.</summary>
    /// <param name="exchange">The exchange; empty for the default exchange (routing key = queue name).</param>
    /// <param name="routingKey">The routing key.</param>
    /// <param name="body">The body.</param>
    /// <param name="properties">The properties.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the broker has confirmed the message.</returns>
    /// <exception cref="BrokerUnavailableException">The broker is unreachable, refused the message, or did not confirm it in time.</exception>
    public async Task PublishRawAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, BasicProperties properties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentNullException.ThrowIfNull(properties);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw NotConfirmed();
        }

        try
        {
            var channel = await ChannelAsync(timeout.Token).ConfigureAwait(false);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DropChannelAsync().ConfigureAwait(false);
            throw NotConfirmed();
        }
        catch (Exception ex) when (ex is PublishException or AlreadyClosedException or OperationInterruptedException)
        {
            await DropChannelAsync().ConfigureAwait(false);
            throw new BrokerUnavailableException($"The message broker at {_connection.Endpoint} did not accept the message.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DropChannelAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // A producer span under the message's own trace context; the message's traceparent is sent as it is.
    private async Task TracedPublishAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        BasicProperties properties,
        Guid messageId,
        string messageType,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        using var span = MessagingTracing.StartPublish(exchange.Length > 0 ? exchange : routingKey, routingKey, messageId, messageType, traceparent);
        try
        {
            await PublishRawAsync(exchange, routingKey, body, properties, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            throw;
        }
    }

    private static BasicProperties Properties(Guid messageId, string type, int schemaVersion, DateTimeOffset occurredAt, string? traceparent)
    {
        var headers = new Dictionary<string, object?> { [SchemaVersionHeader] = schemaVersion };
        if (traceparent is not null)
        {
            headers[TraceparentHeader] = traceparent;
        }

        return new BasicProperties
        {
            MessageId = messageId.ToString(),
            Type = type,
            ContentType = JsonContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(occurredAt.ToUnixTimeSeconds()),
            Headers = headers,
        };
    }

    private BrokerUnavailableException NotConfirmed() =>
        new($"The message broker at {_connection.Endpoint} did not confirm the message within {_timeout.TotalSeconds:0.#} s.", new TimeoutException());

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await DropChannelAsync().ConfigureAwait(false);
        _channel = await _connection.Current.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        return _channel;
    }

    private async Task DropChannelAsync()
    {
        if (_channel is { } channel)
        {
            _channel = null;
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException)
            {
                // Already gone with the connection; nothing to release.
            }
        }
    }
}
