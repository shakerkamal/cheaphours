using System.Text;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Messaging;

public sealed class TopologyDeclarerTests(RabbitMqFixture rabbit) : IAsyncLifetime
{
    private readonly RabbitMqConnection _connection = new(rabbit.ConnectionString, "topology-tests", NullLogger<RabbitMqConnection>.Instance);

    public async ValueTask InitializeAsync() => await _connection.ConnectAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static MessagingTopology NewTopology() =>
        new(new MessagingOptions { QueuePrefix = RabbitMqFixture.NewPrefix(nameof(TopologyDeclarerTests)) });

    [Fact]
    public async Task Declaring_twice_succeeds_and_creates_each_queue_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = NewTopology();

        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);
        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);

        await using var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct);
        foreach (var queue in topology.Queues)
        {
            var ok = await channel.QueueDeclarePassiveAsync(queue.Name, ct);
            Assert.Equal(0u, ok.MessageCount);
        }

        Assert.Equal(5, topology.Queues.Count);
    }

    [Fact]
    public async Task The_binding_routes_telemetry_to_its_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = NewTopology();
        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);

        await using var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicPublishAsync(topology.EventsExchange, TelemetryReceived.TypeName, Encoding.UTF8.GetBytes("{}"), ct);

        BasicGetResult? got = null;
        for (var i = 0; i < 50 && got is null; i++)
        {
            got = await channel.BasicGetAsync(topology.Telemetry.Main, autoAck: true, ct);
            if (got is null)
            {
                await Task.Delay(100, ct);
            }
        }

        Assert.NotNull(got);
    }

    [Fact]
    public async Task A_queue_with_other_arguments_stops_the_declaration_and_is_named()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = NewTopology();
        await using (var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct))
        {
            await channel.QueueDeclareAsync(topology.Telemetry.Main, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct));

        Assert.Contains($"'{topology.Telemetry.Main}'", error.Message, StringComparison.Ordinal);
    }
}
