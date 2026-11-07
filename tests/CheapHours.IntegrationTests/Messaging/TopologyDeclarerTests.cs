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

        Assert.Equal(20, topology.Queues.Count);
    }

    [Fact]
    public async Task The_binding_routes_prices_to_the_planner_and_not_to_telemetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = NewTopology();
        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);

        await using var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicPublishAsync(topology.EventsExchange, PricesPublished.TypeName, Encoding.UTF8.GetBytes("{}"), ct);

        BasicGetResult? got = null;
        for (var i = 0; i < 50 && got is null; i++)
        {
            got = await channel.BasicGetAsync(topology.Planner.Main, autoAck: true, ct);
            if (got is null)
            {
                await Task.Delay(100, ct);
            }
        }

        Assert.NotNull(got);
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(topology.Telemetry.Main, ct)).MessageCount);
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

    // message-delivery: "Binding routes commands and their results".
    [Fact]
    public async Task Commands_and_their_results_are_routed_to_their_own_queues()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = NewTopology();
        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);

        await using var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicPublishAsync(topology.EventsExchange, CommandRequested.TypeName, Encoding.UTF8.GetBytes("{\"m\":1}"), ct);
        await channel.BasicPublishAsync(topology.EventsExchange, GatewayCommandResult.TypeName, Encoding.UTF8.GetBytes("{\"m\":2}"), ct);

        Assert.Equal("{\"m\":1}", Encoding.UTF8.GetString((await GetAsync(channel, topology.Dispatch.Main)).Body.Span));
        Assert.Equal("{\"m\":2}", Encoding.UTF8.GetString((await GetAsync(channel, topology.CommandResults.Main)).Body.Span));
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(topology.Dispatch.Main, ct)).MessageCount);
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(topology.Telemetry.Main, ct)).MessageCount);
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(topology.Planner.Main, ct)).MessageCount);
    }

    // message-delivery: "Replica queue missing".
    [Fact]
    public async Task Sending_to_a_queue_that_does_not_exist_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new MessagingOptions { QueuePrefix = RabbitMqFixture.NewPrefix(nameof(TopologyDeclarerTests)) };
        var topology = new MessagingTopology(options);
        await TopologyDeclarer.DeclareAsync(_connection.Current, topology, ct);
        await using var publisher = new RabbitMqPublisher(_connection, topology, Microsoft.Extensions.Options.Options.Create(options));

        await Assert.ThrowsAsync<BrokerUnavailableException>(() =>
            publisher.SendToQueueAsync(GatewayCommandSample(), GatewayCommand.DestinationFor($"{options.QueuePrefix}gw-9"), ct));

        // The same publisher still works afterwards, and a queue that exists receives the message.
        await publisher.SendToQueueAsync(GatewayCommandSample(), topology.Dispatch.Main, ct);
        await using var channel = await _connection.Current.CreateChannelAsync(cancellationToken: ct);
        var got = await GetAsync(channel, topology.Dispatch.Main);
        Assert.Equal(GatewayCommand.TypeName, got.BasicProperties.Type);
    }

    private static GatewayCommand GatewayCommandSample() => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        null,
        "CP-01",
        "gw-9",
        new Core.Commands.DeviceCommand(
            Core.Ids.CommandId.Parse("cmd_topology"),
            Core.Ids.DeviceId.Parse("dev_wb"),
            Core.Commands.CommandType.StartCharging,
            "key_topology"));

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, TestContext.Current.CancellationToken) is { } got)
            {
                return got;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No message arrived in {queue}.");
    }
}
