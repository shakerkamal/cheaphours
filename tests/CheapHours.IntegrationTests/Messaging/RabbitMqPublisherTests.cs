using System.Diagnostics;
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

public sealed class RabbitMqPublisherTests(RabbitMqFixture rabbit)
{
    private static MessagingOptions NewOptions() => new() { QueuePrefix = RabbitMqFixture.NewPrefix(nameof(RabbitMqPublisherTests)) };

    private static async Task<(RabbitMqConnection Connection, RabbitMqPublisher Publisher, MessagingTopology Topology)> StartAsync(string connectionString)
    {
        var options = NewOptions();
        var topology = new MessagingTopology(options);
        var connection = new RabbitMqConnection(connectionString, "publisher-tests", NullLogger<RabbitMqConnection>.Instance);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        await TopologyDeclarer.DeclareAsync(connection.Current, topology, TestContext.Current.CancellationToken);
        return (connection, new RabbitMqPublisher(connection, topology, Options.Create(options)), topology);
    }

    private static async Task<BasicGetResult> GetAsync(IConnection connection, string queue)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        for (var i = 0; i < 50; i++)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, ct) is { } got)
            {
                return got;
            }

            await Task.Delay(100, ct);
        }

        throw new Xunit.Sdk.XunitException($"No message arrived in '{queue}'.");
    }

    [Fact]
    public async Task A_confirmed_message_is_in_its_queue_with_the_envelope_in_its_properties()
    {
        var (connection, publisher, topology) = await StartAsync(rabbit.ConnectionString);
        await using var _ = connection;
        await using var __ = publisher;
        var message = TestMessages.Telemetry(TestMessages.NewDevice(), readings: 2, traceparent: TestMessages.Traceparent);

        await publisher.PublishAsync(message, TestContext.Current.CancellationToken);

        var got = await GetAsync(connection.Current, topology.Telemetry.Main);
        var properties = got.BasicProperties;
        Assert.Equal(message.MessageId.ToString(), properties.MessageId);
        Assert.Equal(TelemetryReceived.TypeName, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal(1, Convert.ToInt32(properties.Headers![RabbitMqPublisher.SchemaVersionHeader], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TestMessages.Traceparent, Encoding.UTF8.GetString((byte[])properties.Headers[RabbitMqPublisher.TraceparentHeader]!));

        using var body = JsonDocument.Parse(got.Body);
        Assert.Equal(properties.MessageId, body.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(properties.Type, body.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(TestMessages.Traceparent, body.RootElement.GetProperty("traceparent").GetString());
        Assert.Equal(JsonSerializer.Serialize(message, CheapHoursJson.Options), Encoding.UTF8.GetString(got.Body.Span));
    }

    [Fact]
    public async Task Publishing_while_the_broker_is_stopped_fails_within_five_seconds()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        var (connection, publisher, _) = await StartAsync(broker.ConnectionString);
        await using var c = connection;
        await using var p = publisher;
        await broker.StopAsync();

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<BrokerUnavailableException>(() => publisher.PublishAsync(TestMessages.Telemetry(TestMessages.NewDevice()), TestContext.Current.CancellationToken));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task A_published_message_survives_a_broker_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        var (connection, publisher, topology) = await StartAsync(broker.ConnectionString);
        await using (connection)
        await using (publisher)
        {
            await publisher.PublishAsync(TestMessages.Telemetry(TestMessages.NewDevice()), ct);
        }

        await broker.StopAsync();
        await broker.StartAsync();

        var factory = new ConnectionFactory { Uri = new Uri(broker.ConnectionString) };
        await using var fresh = await factory.CreateConnectionAsync(ct);
        await using var channel = await fresh.CreateChannelAsync(cancellationToken: ct);
        Assert.Equal(1u, (await channel.QueueDeclarePassiveAsync(topology.Telemetry.Main, ct)).MessageCount);
    }
}
