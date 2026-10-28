using System.Net;
using System.Net.Sockets;
using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Telemetry;
using MongoDB.Bson;
using Testcontainers.MongoDb;

namespace CheapHours.IntegrationTests.Messaging;

public sealed class ConsumerOutageTests(RabbitMqFixture rabbit)
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_database_outage_pauses_consumption_without_spending_retries()
    {
        var ct = TestContext.Current.CancellationToken;

        // A MongoDB of its own, on a fixed port so it comes back at the same address after a stop.
        var port = FreePort();
        await using var mongo = new MongoDbBuilder(MongoFixture.Image).WithReplicaSet().WithPortBinding(port, 27017).Build();
        await mongo.StartAsync(ct);
        var settings = MongoClientSettings.FromConnectionString(mongo.GetConnectionString());
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
        settings.SocketTimeout = TimeSpan.FromSeconds(3);
        var database = new MongoClient(settings).GetDatabase("outage");
        await MongoIndexes.EnsureAsync(database, ct);

        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(ConsumerOutageTests));
        harness.Consume(new TelemetryBatchHandler(new TelemetryBatchWriter(database, TimeProvider.System)));

        await mongo.StopAsync(ct);
        var device = TestMessages.NewDevice();
        var messages = Enumerable.Range(0, 20).Select(i => TestMessages.Telemetry(device, readings: 2, startSeconds: i * 30)).ToList();
        foreach (var message in messages)
        {
            await harness.Publisher.PublishAsync(message, ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Telemetry.DeadLetter));
        foreach (var retry in harness.Topology.Telemetry.Retries)
        {
            Assert.Equal(0u, await harness.CountAsync(retry));
        }

        await mongo.StartAsync(ct);

        var telemetry = database.GetCollection<BsonDocument>(MongoIndexes.Telemetry);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long stored = 0;
        while (DateTime.UtcNow < deadline && stored < 40)
        {
            try
            {
                stored = await telemetry.CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Value), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is TimeoutException or MongoConnectionException)
            {
                // Still coming back up.
            }

            await Task.Delay(250, ct);
        }

        await Task.Delay(1000, ct);
        Assert.Equal(40, await telemetry.CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Value), cancellationToken: ct));
        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Telemetry.DeadLetter));
    }

    [Fact]
    public async Task Consumption_resumes_after_the_broker_restarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        await using var harness = await ConsumerHarness.StartAsync(broker.ConnectionString, nameof(ConsumerOutageTests));
        var handler = new RecordingHandler();
        harness.Consume(handler);
        var before = TestMessages.Telemetry(TestMessages.NewDevice());
        await harness.Publisher.PublishAsync(before, ct);
        await ConsumerHarness.EventuallyAsync(() => handler.TimesHandled(before.MessageId) == 1, TimeSpan.FromSeconds(10), "the first message");

        await broker.StopAsync();
        await broker.StartAsync();
        var back = DateTime.UtcNow;

        var after = TestMessages.Telemetry(TestMessages.NewDevice());
        while (true)
        {
            try
            {
                await harness.Publisher.PublishAsync(after, ct);
                break;
            }
            catch (CheapHours.Messaging.BrokerUnavailableException) when (DateTime.UtcNow - back < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(500, ct);
            }
        }

        await ConsumerHarness.EventuallyAsync(() => handler.TimesHandled(after.MessageId) == 1, TimeSpan.FromSeconds(30), "consumption to resume");
        Assert.True(DateTime.UtcNow - back < TimeSpan.FromSeconds(30));
    }
}
