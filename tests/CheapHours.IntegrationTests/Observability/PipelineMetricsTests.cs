using System.Diagnostics.Metrics;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Locks;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// Outbox lag on the relaying worker and stored telemetry readings (operational-metrics: "Outbox lag is reported",
/// "Stored telemetry readings are counted").
/// </summary>
public sealed class PipelineMetricsTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private const string Lag = CommandMetrics.OutboxLagName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // operational-metrics: "Broker down" and "Outbox drained"; a worker without the relay lease reports nothing.
    [Fact]
    public async Task The_relaying_worker_reports_the_age_of_the_oldest_due_entry_and_zero_once_drained()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        var database = MongoFixture.NewDatabaseName(nameof(PipelineMetricsTests));
        var prefix = RabbitMqFixture.NewPrefix(nameof(PipelineMetricsTests));

        // The second worker's relay loop waits an hour before its first pass: it never takes the lease here.
        var relaying = await WorkerHost.StartAsync(mongo, broker.ConnectionString, database, prefix, new Dictionary<string, string> { ["Worker:ReplicaId"] = "worker-a", ["Commands:RelayInterval"] = "00:00:00.200" });
        var idle = await WorkerHost.StartAsync(mongo, broker.ConnectionString, database, prefix, new Dictionary<string, string> { ["Worker:ReplicaId"] = "worker-b", ["Commands:RelayInterval"] = "01:00:00" });
        using var relayingLag = new MetricRecorder(relaying.Services.GetRequiredService<IMeterFactory>(), Lag);
        using var idleLag = new MetricRecorder(idle.Services.GetRequiredService<IMeterFactory>(), Lag);
        try
        {
            await broker.StopAsync();
            var now = DateTimeOffset.UtcNow;
            var command = new StoredCommand(
                Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), DeviceId.New(TimeProvider.System), CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), now),
                OwnerId.Parse("own_demo"),
                null);
            Assert.True(await new CommandStore(mongo.Client.GetDatabase(database)).CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now, availableAt: now.AddSeconds(-90)), Ct));

            // The relay's next pass sees the entry, which became due 90 s ago and cannot be published.
            await UntilAsync(() => relayingLag.Latest(Lag) >= 90, TimeSpan.FromSeconds(30));
            Assert.Null(idleLag.Latest(Lag));

            // While another replica holds the lease, this one's passes do not run and it reports nothing. (Held for
            // longer than the relay's longest back-off, so its next pass meets it.)
            var lease = relaying.Services.GetRequiredKeyedService<ILease>(LeaseNames.OutboxRelay);
            await UntilAsync(() => lease.TryAcquireAsync("worker-elsewhere", TimeSpan.FromMinutes(5), Ct).GetAwaiter().GetResult(), TimeSpan.FromSeconds(30));
            await UntilAsync(() => relayingLag.Latest(Lag) is null, TimeSpan.FromSeconds(60));

            // Once the broker is back and the lease free, the entry is published and the next pass reports 0.
            await broker.StartAsync();
            await lease.ReleaseAsync("worker-elsewhere", Ct);
            await UntilAsync(() => relayingLag.Latest(Lag) == 0, TimeSpan.FromSeconds(90));
            Assert.Null(idleLag.Latest(Lag));
        }
        finally
        {
            await StopQuietlyAsync(relaying);
            await StopQuietlyAsync(idle);
        }
    }

    // operational-metrics: "Redelivered message".
    [Fact]
    public async Task A_message_delivered_twice_counts_its_three_readings_once()
    {
        var worker = await WorkerHost.StartAsync(mongo, rabbit, MongoFixture.NewDatabaseName(nameof(PipelineMetricsTests)), RabbitMqFixture.NewPrefix(nameof(PipelineMetricsTests)), new Dictionary<string, string>());
        try
        {
            using var readings = new MetricRecorder(worker.Services.GetRequiredService<IMeterFactory>(), TelemetryMetrics.ReadingsName);
            var handler = worker.Services.GetRequiredService<TelemetryBatchHandler>();
            var now = DateTimeOffset.UtcNow;
            var message = new TelemetryReceived(
                Guid.CreateVersion7(),
                now,
                null,
                DeviceId.New(TimeProvider.System),
                "ocpp",
                [.. Enumerable.Range(0, 3).Select(i => new DeviceReading("CP001", now.AddSeconds(i * 15), 7400, 1000 + i))]);

            await handler.HandleAsync([new Delivered<TelemetryReceived>(message, IsRepeat: false)], Ct);
            await handler.HandleAsync([new Delivered<TelemetryReceived>(message, IsRepeat: true)], Ct);

            Assert.Equal(3, readings.Sum(TelemetryMetrics.ReadingsName, ("provider.id", "ocpp")));
            Assert.All(readings.All, m => Assert.Equal(["provider.id"], m.Tags.Keys));
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not reached within {within}.");
            await Task.Delay(200, Ct);
        }
    }

    // A worker whose broker went away may take a moment to stop; the test's outcome does not depend on it.
    private static async Task StopQuietlyAsync(IHost host)
    {
        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            host.Dispose();
        }
    }
}
