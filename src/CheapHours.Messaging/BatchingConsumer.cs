using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CheapHours.Messaging;

/// <summary>
/// Consumes one queue in batches and settles every message one way or another (ADR-003):
/// <list type="bullet">
/// <item>handled → acknowledged;</item>
/// <item>unreadable or a newer schema version → dead-lettered at once;</item>
/// <item>the handler throws → the batch is split and each message handled alone; one that still fails goes
/// to its next delay queue, and after the fourth attempt to the dead-letter queue;</item>
/// <item>a dependency is down (<see cref="DependencyUnavailableException"/>) → the batch stays
/// unacknowledged and is handed over again, as a repeat, with backoff up to 30 s; no retry is spent.</item>
/// </list>
/// </summary>
/// <typeparam name="TMessage">The message type the queue carries.</typeparam>
public sealed class BatchingConsumer<TMessage>
    where TMessage : Message
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqPublisher _publisher;
    private readonly ConsumerQueues _queues;
    private readonly IBatchHandler<TMessage> _handler;
    private readonly int _supportedSchemaVersion;
    private readonly MessagingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the consumer.</summary>
    /// <param name="connection">The broker connection; it must be open when <see cref="RunAsync"/> starts.</param>
    /// <param name="publisher">The publisher used for retry and dead-letter copies.</param>
    /// <param name="queues">The queues to consume and route to.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="supportedSchemaVersion">The highest schema version the handler understands.</param>
    /// <param name="options">The messaging options.</param>
    /// <param name="time">The clock for batching windows and backoff.</param>
    /// <param name="logger">The logger.</param>
    public BatchingConsumer(
        RabbitMqConnection connection,
        RabbitMqPublisher publisher,
        ConsumerQueues queues,
        IBatchHandler<TMessage> handler,
        int supportedSchemaVersion,
        IOptions<MessagingOptions> options,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(supportedSchemaVersion, 1);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _publisher = publisher;
        _queues = queues;
        _handler = handler;
        _supportedSchemaVersion = supportedSchemaVersion;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets a step run after a batch is handled and before it is acknowledged. Tests use it to stop
    /// the consumer at the worst moment.
    /// </summary>
    internal Func<IChannel, CancellationToken, Task>? AfterHandledForTests { get; set; }

    /// <summary>Consumes until <paramref name="cancellationToken"/> is cancelled; unacknowledged messages then return to the queue.</summary>
    /// <param name="cancellationToken">Stops consuming.</param>
    /// <returns>A task that completes when consuming has stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var channel = await _connection.Current.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            await channel.BasicQosAsync(0, (ushort)_options.Prefetch, global: false, cancellationToken).ConfigureAwait(false);

            // Delivery tags start again from 1 after the connection recovers; a delivery from before that
            // must never be acknowledged by tag, so each one remembers the channel generation it came from.
            var state = new ChannelState();
            channel.ChannelShutdownAsync += (_, _) =>
            {
                state.NextGeneration();
                return Task.CompletedTask;
            };

            var buffer = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(_options.Prefetch) { SingleReader = true });
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivered) =>
            {
                var delivery = new Delivery(delivered.DeliveryTag, state.Generation, delivered.Body.ToArray(), delivered.Redelivered, AttemptOf(delivered.BasicProperties), delivered.BasicProperties);
                await buffer.Writer.WriteAsync(delivery, cancellationToken).ConfigureAwait(false);
            };

            await channel.BasicConsumeAsync(_queues.Main, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
            ConsumerLog.Started(_logger, _queues.Main);

            try
            {
                while (true)
                {
                    var batch = await ReadBatchAsync(buffer.Reader, cancellationToken).ConfigureAwait(false);
                    await ProcessAsync(channel, state, batch, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ConsumerLog.Stopped(_logger, _queues.Main);
            }
        }
    }

    private static int AttemptOf(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not { } headers || !headers.TryGetValue(ConsumerHeaders.Attempt, out var value) || value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static BasicProperties CopyWith(IReadOnlyBasicProperties original, IEnumerable<KeyValuePair<string, object?>> headers)
    {
        var copy = new BasicProperties(original)
        {
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>(original.Headers ?? new Dictionary<string, object?>()),
        };

        foreach (var (key, value) in headers)
        {
            copy.Headers[key] = value;
        }

        return copy;
    }

    private async Task<List<Delivery>> ReadBatchAsync(ChannelReader<Delivery> reader, CancellationToken cancellationToken)
    {
        var batch = new List<Delivery> { await reader.ReadAsync(cancellationToken).ConfigureAwait(false) };

        using var window = new CancellationTokenSource(_options.BatchWait, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
        while (batch.Count < _options.BatchSize)
        {
            if (reader.TryRead(out var next))
            {
                batch.Add(next);
                continue;
            }

            try
            {
                if (!await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return batch;
    }

    private async Task ProcessAsync(IChannel channel, ChannelState state, List<Delivery> batch, CancellationToken cancellationToken)
    {
        var valid = new List<(Delivery Delivery, TMessage Message)>();
        foreach (var delivery in batch)
        {
            if (Read(delivery, out var message) is { } category)
            {
                await ParkAsync(channel, state, delivery, category, delivery.Attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                valid.Add((delivery, message!));
            }
        }

        if (valid.Count == 0)
        {
            return;
        }

        try
        {
            await HandleThroughOutagesAsync(valid, repeat: false, cancellationToken).ConfigureAwait(false);
            if (AfterHandledForTests is { } hook)
            {
                await hook(channel, cancellationToken).ConfigureAwait(false);
            }

            await AckAsync(channel, state, valid.Select(v => v.Delivery).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && valid.Count == 1)
        {
            // A batch of one has already had its attempt.
            ConsumerLog.MessageFailed(_logger, _queues.Main, valid[0].Message.MessageId, valid[0].Delivery.Attempt + 1, ex.GetType().Name);
            await RetryOrParkAsync(channel, state, valid[0].Delivery, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad message must not hold back the rest: handle each alone; part of the batch may have
            // been written, so every single one counts as a repeat.
            ConsumerLog.BatchFailed(_logger, _queues.Main, valid.Count, ex.GetType().Name);
            foreach (var item in valid)
            {
                await HandleAloneAsync(channel, state, item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAloneAsync(IChannel channel, ChannelState state, (Delivery Delivery, TMessage Message) item, CancellationToken cancellationToken)
    {
        try
        {
            await HandleThroughOutagesAsync([item], repeat: true, cancellationToken).ConfigureAwait(false);
            await AckAsync(channel, state, [item.Delivery], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsumerLog.MessageFailed(_logger, _queues.Main, item.Message.MessageId, item.Delivery.Attempt + 1, ex.GetType().Name);
            await RetryOrParkAsync(channel, state, item.Delivery, cancellationToken).ConfigureAwait(false);
        }
    }

    // A dependency outage is not the message's fault: keep the batch, wait, and hand it over again.
    private async Task HandleThroughOutagesAsync(IReadOnlyList<(Delivery Delivery, TMessage Message)> items, bool repeat, CancellationToken cancellationToken)
    {
        var backoff = FirstBackoff;
        while (true)
        {
            var batch = items
                .Select(i => new Delivered<TMessage>(i.Message, repeat || i.Delivery.Redelivered || i.Delivery.Attempt > 0))
                .ToList();
            try
            {
                await _handler.HandleAsync(batch, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DependencyUnavailableException)
            {
                ConsumerLog.DependencyDown(_logger, _queues.Main, items.Count, backoff);
                await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                repeat = true;
            }
        }
    }

    private string? Read(Delivery delivery, out TMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(delivery.Body);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
            {
                return ConsumerHeaders.Unreadable;
            }

            if (number > _supportedSchemaVersion)
            {
                return ConsumerHeaders.UnsupportedVersion;
            }

            message = document.RootElement.Deserialize<TMessage>(CheapHoursJson.Options);
            return message is null ? ConsumerHeaders.Unreadable : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or NotSupportedException or InvalidOperationException)
        {
            // Includes a body that parses but breaks the message's own invariants (for example no readings).
            return ConsumerHeaders.Unreadable;
        }
    }

    private async Task RetryOrParkAsync(IChannel channel, ChannelState state, Delivery delivery, CancellationToken cancellationToken)
    {
        var next = delivery.Attempt + 1;
        if (next > _queues.Retries.Count)
        {
            await ParkAsync(channel, state, delivery, ConsumerHeaders.HandlerFailed, delivery.Attempt + 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        var properties = CopyWith(delivery.Properties, [new(ConsumerHeaders.Attempt, next)]);
        await RouteAsync(channel, state, delivery, _queues.Retries[next - 1], properties, cancellationToken).ConfigureAwait(false);
    }

    private async Task ParkAsync(IChannel channel, ChannelState state, Delivery delivery, string category, int attempts, CancellationToken cancellationToken)
    {
        var properties = CopyWith(delivery.Properties,
        [
            new(ConsumerHeaders.Attempts, attempts),
            new(ConsumerHeaders.FailureCategory, category),
            new(ConsumerHeaders.FailedAt, _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        ]);

        if (await RouteAsync(channel, state, delivery, _queues.DeadLetter, properties, cancellationToken).ConfigureAwait(false))
        {
            ConsumerLog.DeadLettered(_logger, _queues.Main, delivery.Properties.MessageId ?? "(none)", category, attempts);
        }
    }

    // The copy is confirmed before the original is acknowledged, so a crash in between duplicates rather
    // than loses (the inbox drops the duplicate). If the broker will not take the copy, the original goes
    // back to the queue.
    private async Task<bool> RouteAsync(IChannel channel, ChannelState state, Delivery delivery, string queue, BasicProperties properties, CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishRawAsync(string.Empty, queue, delivery.Body, properties, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            await NackAsync(channel, state, delivery, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await AckAsync(channel, state, [delivery], cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task AckAsync(IChannel channel, ChannelState state, IReadOnlyList<Delivery> deliveries, CancellationToken cancellationToken)
    {
        var current = deliveries.Where(d => d.Generation == state.Generation).ToList();
        if (current.Count == 0)
        {
            return;
        }

        try
        {
            // Batches are processed in delivery order and everything before them is already settled, so one
            // multiple-ack at the highest tag settles exactly this batch.
            await channel.BasicAckAsync(current.Max(d => d.Tag), multiple: current.Count > 1, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException)
        {
            // The channel went away first; the broker redelivers and the inbox drops what was already handled.
            ConsumerLog.AckLost(_logger, _queues.Main, current.Count);
        }
    }

    private async Task NackAsync(IChannel channel, ChannelState state, Delivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Generation != state.Generation)
        {
            return;
        }

        try
        {
            await channel.BasicNackAsync(delivery.Tag, multiple: false, requeue: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException)
        {
            ConsumerLog.AckLost(_logger, _queues.Main, 1);
        }
    }

    private sealed record Delivery(ulong Tag, long Generation, byte[] Body, bool Redelivered, int Attempt, IReadOnlyBasicProperties Properties);

    private sealed class ChannelState
    {
        private long _generation;

        public long Generation => Interlocked.Read(ref _generation);

        public void NextGeneration() => Interlocked.Increment(ref _generation);
    }
}
