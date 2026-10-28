using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Messaging;
using CheapHours.Messaging;
using CheapHours.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Worker;

public sealed class WorkerTelemetryTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private readonly string _database = MongoFixture.NewDatabaseName(nameof(WorkerTelemetryTests));
    private readonly string _prefix = RabbitMqFixture.NewPrefix(nameof(WorkerTelemetryTests));

    private IMongoCollection<BsonDocument> Telemetry => mongo.Client.GetDatabase(_database).GetCollection<BsonDocument>(MongoIndexes.Telemetry);

    private async Task<IHost> StartWorkerAsync(TelemetryConsumerHooks? hooks = null)
    {
        string[] args =
        [
            $"--ConnectionStrings:Mongo={mongo.ConnectionString}",
            $"--Mongo:Database={_database}",
            $"--ConnectionStrings:RabbitMq={rabbit.ConnectionString}",
            $"--Messaging:QueuePrefix={_prefix}",
        ];
        var host = WorkerRole.Build(args, services =>
        {
            if (hooks is not null)
            {
                services.AddSingleton(hooks);
            }
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.Services.GetRequiredService<WorkerState>().Ready.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
    }

    private async Task<(RabbitMqConnection Connection, RabbitMqPublisher Publisher)> PublisherAsync()
    {
        var options = new MessagingOptions { QueuePrefix = _prefix };
        var connection = new RabbitMqConnection(rabbit.ConnectionString, "worker-tests", NullLogger<RabbitMqConnection>.Instance);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        return (connection, new RabbitMqPublisher(connection, new MessagingTopology(options), Options.Create(options)));
    }

    private async Task<long> CountAsync(Core.Ids.DeviceId device) =>
        await Telemetry.CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Value), cancellationToken: TestContext.Current.CancellationToken);

    private async Task WaitForCountAsync(Core.Ids.DeviceId device, long expected, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (await CountAsync(device) < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_burst_of_a_thousand_messages_is_stored_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = await StartWorkerAsync();
        var (connection, publisher) = await PublisherAsync();
        await using var _ = connection;
        await using var __ = publisher;
        var device = TestMessages.NewDevice();

        for (var i = 0; i < 1000; i++)
        {
            await publisher.PublishAsync(TestMessages.Telemetry(device, startSeconds: i * 15), ct);
        }

        await WaitForCountAsync(device, 1000, TimeSpan.FromSeconds(60));
        await Task.Delay(1000, ct);
        Assert.Equal(1000, await CountAsync(device));
        await StopAsync(worker);
    }

    [Fact]
    public async Task A_duplicate_message_gives_one_reading()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = await StartWorkerAsync();
        var (connection, publisher) = await PublisherAsync();
        await using var _ = connection;
        await using var __ = publisher;
        var device = TestMessages.NewDevice();
        var message = TestMessages.Telemetry(device);

        await publisher.PublishAsync(message, ct);
        await publisher.PublishAsync(message, ct);

        await WaitForCountAsync(device, 1, TimeSpan.FromSeconds(10));
        await Task.Delay(1500, ct);
        Assert.Equal(1, await CountAsync(device));
        await StopAsync(worker);
    }

    [Fact]
    public async Task Two_workers_start_against_the_same_broker_and_database()
    {
        var first = await StartWorkerAsync();
        var second = await StartWorkerAsync();

        await StopAsync(second);
        await StopAsync(first);
    }

    [Fact]
    public async Task A_worker_stopped_after_storing_and_before_acknowledging_leaves_each_reading_stored_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var fired = new TaskCompletionSource();
        var hooks = new TelemetryConsumerHooks
        {
            AfterHandled = async (channel, _) =>
            {
                if (fired.TrySetResult())
                {
                    // The worst moment to die: the readings are stored, the broker has not been told.
                    await channel.DisposeAsync();
                }
            },
        };
        var dying = await StartWorkerAsync(hooks);
        var (connection, publisher) = await PublisherAsync();
        await using var _ = connection;
        await using var __ = publisher;
        var device = TestMessages.NewDevice();

        await publisher.PublishAsync(TestMessages.Telemetry(device, readings: 3), ct);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await StopAsync(dying);

        var next = await StartWorkerAsync();
        await Task.Delay(3000, ct);

        Assert.Equal(3, await CountAsync(device));
        await StopAsync(next);
    }
}
