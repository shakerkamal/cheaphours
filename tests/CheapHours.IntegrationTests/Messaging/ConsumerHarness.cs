using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Messaging;

/// <summary>
/// A real broker connection, topology (own prefix, 200/300/400 ms retry delays), publisher and
/// <see cref="BatchingConsumer{TMessage}"/> running in the background, with a handler the test controls.
/// </summary>
internal sealed class ConsumerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    private ConsumerHarness(RabbitMqConnection connection, MessagingTopology topology, RabbitMqPublisher publisher, MessagingOptions options)
    {
        Connection = connection;
        Topology = topology;
        Publisher = publisher;
        Options = options;
    }

    public RabbitMqConnection Connection { get; }

    public MessagingTopology Topology { get; }

    public RabbitMqPublisher Publisher { get; }

    public MessagingOptions Options { get; }

    public static async Task<ConsumerHarness> StartAsync(string connectionString, string owner, Action<MessagingOptions>? configure = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new MessagingOptions
        {
            QueuePrefix = RabbitMqFixture.NewPrefix(owner),
            FirstRetryDelay = TimeSpan.FromMilliseconds(200),
            SecondRetryDelay = TimeSpan.FromMilliseconds(300),
            ThirdRetryDelay = TimeSpan.FromMilliseconds(400),
        };
        configure?.Invoke(options);
        var topology = new MessagingTopology(options);
        var connection = new RabbitMqConnection(connectionString, owner, NullLogger<RabbitMqConnection>.Instance);
        await connection.ConnectAsync(ct);
        await TopologyDeclarer.DeclareAsync(connection.Current, topology, ct);
        return new ConsumerHarness(connection, topology, new RabbitMqPublisher(connection, topology, Microsoft.Extensions.Options.Options.Create(options)), options);
    }

    public BatchingConsumer<TelemetryReceived> Consume(IBatchHandler<TelemetryReceived> handler, TimeProvider? time = null)
    {
        var consumer = new BatchingConsumer<TelemetryReceived>(
            Connection, Publisher, Topology.Telemetry, handler, TelemetryReceived.CurrentSchemaVersion,
            Microsoft.Extensions.Options.Options.Create(Options), time ?? TimeProvider.System, NullLogger.Instance);
        _run = Task.Run(() => consumer.RunAsync(_stop.Token));
        return consumer;
    }

    public async Task StopConsumingAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run;
        }
    }

    /// <summary>Publishes raw bytes to the main queue, as a broken or foreign producer would.</summary>
    public async Task PublishRawAsync(byte[] body, string messageId)
    {
        var properties = new BasicProperties { MessageId = messageId, Type = TelemetryReceived.TypeName, DeliveryMode = DeliveryModes.Persistent };
        await Publisher.PublishRawAsync(string.Empty, Topology.Telemetry.Main, body, properties, TestContext.Current.CancellationToken);
    }

    /// <summary>Publishes a telemetry message whose body says <paramref name="schemaVersion"/>.</summary>
    public Task PublishWithVersionAsync(TelemetryReceived message, int schemaVersion)
    {
        var json = JsonSerializer.Serialize(message, CheapHoursJson.Options).Replace("\"schemaVersion\":1", $"\"schemaVersion\":{schemaVersion}", StringComparison.Ordinal);
        return PublishRawAsync(Encoding.UTF8.GetBytes(json), message.MessageId.ToString());
    }

    /// <summary>Waits for a message in <paramref name="queue"/> and takes it.</summary>
    public async Task<BasicGetResult> TakeAsync(string queue, TimeSpan? within = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        await using var channel = await Connection.Current.CreateChannelAsync(cancellationToken: ct);
        while (DateTime.UtcNow < deadline)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, ct) is { } got)
            {
                return got;
            }

            await Task.Delay(50, ct);
        }

        throw new Xunit.Sdk.XunitException($"No message arrived in '{queue}'.");
    }

    public async Task<uint> CountAsync(string queue)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var channel = await Connection.Current.CreateChannelAsync(cancellationToken: ct);
        return (await channel.QueueDeclarePassiveAsync(queue, ct)).MessageCount;
    }

    public async ValueTask DisposeAsync()
    {
        await StopConsumingAsync();
        await Publisher.DisposeAsync();
        await Connection.DisposeAsync();
        _stop.Dispose();
    }

    /// <summary>Waits until <paramref name="condition"/> holds.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan within, string what)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A handler that records every call and fails when the test says so.</summary>
internal sealed class RecordingHandler : IBatchHandler<TelemetryReceived>
{
    private readonly ConcurrentQueue<(DateTime At, IReadOnlyList<Delivered<TelemetryReceived>> Batch)> _calls = new();
    private readonly ConcurrentDictionary<Guid, int> _handled = new();

    /// <summary>Gets or sets a check run for each call; throwing from it fails the call.</summary>
    public Action<IReadOnlyList<Delivered<TelemetryReceived>>>? Before { get; set; }

    public IReadOnlyList<(DateTime At, IReadOnlyList<Delivered<TelemetryReceived>> Batch)> Calls => _calls.ToArray();

    public int TimesHandled(Guid messageId) => _handled.GetValueOrDefault(messageId);

    public int HandledCount => _handled.Count;

    public Task HandleAsync(IReadOnlyList<Delivered<TelemetryReceived>> batch, CancellationToken cancellationToken)
    {
        _calls.Enqueue((DateTime.UtcNow, batch));
        Before?.Invoke(batch);
        foreach (var delivered in batch)
        {
            _handled.AddOrUpdate(delivered.Message.MessageId, 1, (_, n) => n + 1);
        }

        return Task.CompletedTask;
    }
}
