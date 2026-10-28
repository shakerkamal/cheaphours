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

        var headers = new Dictionary<string, object?> { [SchemaVersionHeader] = message.SchemaVersion };
        if (message.Traceparent is { } traceparent)
        {
            headers[TraceparentHeader] = traceparent;
        }

        return new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            Type = message.MessageType,
            ContentType = JsonContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = headers,
        };
    }

    /// <inheritdoc/>
    public Task PublishAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), CheapHoursJson.Options);
        return PublishRawAsync(_topology.EventsExchange, message.MessageType, body, PropertiesFor(message), cancellationToken);
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
