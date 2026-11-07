using System.Text;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// A durable queue of its own bound to a test's events exchange, so a test sees every message published with a
/// routing key without taking it from the application's consumers. It survives a broker restart.
/// </summary>
internal sealed class EventsObserver : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queue;
    private readonly List<(string MessageId, string Body)> _seen = [];

    private EventsObserver(IConnection connection, IChannel channel, string queue)
    {
        _connection = connection;
        _channel = channel;
        _queue = queue;
    }

    /// <summary>Gets the message ids seen so far, in arrival order.</summary>
    public IReadOnlyList<string> MessageIds => [.. _seen.Select(s => s.MessageId)];

    /// <summary>Gets the bodies seen so far, in arrival order.</summary>
    public IReadOnlyList<string> Bodies => [.. _seen.Select(s => s.Body)];

    /// <summary>Declares the events exchange (as the roles do) and a queue bound to it with <paramref name="routingKey"/>.</summary>
    public static async Task<EventsObserver> StartAsync(string connectionString, string prefix, string routingKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await new ConnectionFactory { Uri = new Uri(connectionString) }.CreateConnectionAsync(ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var exchange = prefix + "cheaphours.events";
        var queue = $"{prefix}test.observer.{routingKey}";
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(queue, exchange, routingKey, cancellationToken: ct);
        return new EventsObserver(connection, channel, queue);
    }

    /// <summary>Takes every message waiting in the queue into <see cref="MessageIds"/>.</summary>
    /// <returns>How many messages were seen in total.</returns>
    public async Task<int> DrainAsync()
    {
        while (await _channel.BasicGetAsync(_queue, autoAck: true, TestContext.Current.CancellationToken) is { } got)
        {
            _seen.Add((got.BasicProperties.MessageId ?? string.Empty, Encoding.UTF8.GetString(got.Body.Span)));
        }

        return _seen.Count;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is RabbitMQ.Client.Exceptions.AlreadyClosedException or ObjectDisposedException)
        {
        }
    }
}
