using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// Sends gateway commands to a test gateway's replica queue, as the worker's gateway route does, and reads the
/// results the replica publishes to the events exchange.
/// </summary>
internal sealed class GatewayCommands : IAsyncDisposable
{
    private readonly GatewayFactory _gateway;
    private readonly EventsObserver _results;

    private GatewayCommands(GatewayFactory gateway, EventsObserver results)
    {
        _gateway = gateway;
        _results = results;
    }

    public string Queue => _gateway.Topology.GatewayCommandQueue(GatewayFactory.ReplicaId);

    /// <summary>Waits until the replica consumes its queue, then starts reading results.</summary>
    public static async Task<GatewayCommands> StartAsync(GatewayFactory gateway)
    {
        var results = await EventsObserver.StartAsync(RabbitMqFixture.SharedConnectionString, gateway.QueuePrefix, GatewayCommandResult.TypeName);
        var commands = new GatewayCommands(gateway, results);
        await commands.WaitForConsumerAsync();
        return commands;
    }

    public static DeviceCommand Command(CommandType type, int? maxPowerW = null) =>
        new(CommandId.New(TimeProvider.System), DeviceId.Parse("dev_cp001"), type, $"key_{Guid.NewGuid():N}", maxPowerW);

    public Task SendAsync(DeviceCommand command, string chargePointId = "CP001") =>
        _gateway.Services.GetRequiredService<IMessagePublisher>().SendToQueueAsync(
            new GatewayCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow, null, chargePointId, GatewayFactory.ReplicaId, command),
            Queue,
            TestContext.Current.CancellationToken);

    /// <summary>Waits for the results of <paramref name="commandId"/> until <paramref name="count"/> have arrived.</summary>
    public async Task<IReadOnlyList<SendResult>> ResultsAsync(CommandId commandId, int count = 1)
    {
        for (var i = 0; i < 150; i++)
        {
            await _results.DrainAsync();
            var mine = _results.Bodies
                .Select(b => JsonSerializer.Deserialize<GatewayCommandResult>(b, CheapHoursJson.Options)!)
                .Where(r => r.CommandId == commandId)
                .Select(r => r.Outcome)
                .ToArray();
            if (mine.Length >= count)
            {
                return mine;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Fewer than {count} result(s) for {commandId} arrived.");
    }

    public async Task<uint> ConsumerCountAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(RabbitMqFixture.SharedConnectionString) }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (await channel.QueueDeclarePassiveAsync(Queue, TestContext.Current.CancellationToken)).ConsumerCount;
    }

    public ValueTask DisposeAsync() => _results.DisposeAsync();

    private async Task WaitForConsumerAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if (await ConsumerCountAsync() == 1)
                {
                    return;
                }
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // Not declared yet.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{Queue} has no consumer.");
    }
}
