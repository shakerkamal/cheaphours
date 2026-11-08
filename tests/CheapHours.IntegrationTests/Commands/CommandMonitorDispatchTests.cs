using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// Re-announcing and dead-lettering by the command monitor (command-dispatch spec: a lost message does not strand
/// a command; a device handles one command at a time).
/// </summary>
public sealed class CommandMonitorDispatchTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static readonly Dictionary<string, string> FastMonitor = new()
    {
        ["Commands:MonitorInterval"] = "00:00:00.200",
        ["Commands:DispatchTimeout"] = "00:00:01",
        ["Messaging:FirstRetryDelay"] = "00:00:00.100",
        ["Messaging:SecondRetryDelay"] = "00:00:00.200",
        ["Messaging:ThirdRetryDelay"] = "00:00:00.300",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // command-dispatch: "Message lost".
    [Fact]
    public async Task Lost_request_is_announced_again_and_the_command_reaches_its_device_once()
    {
        // A realistic dispatch timeout: under load the re-announced message may take more than a second to be sent,
        // which must not count against the command here.
        var settings = new Dictionary<string, string>(FastMonitor) { ["Commands:DispatchTimeout"] = "00:00:30" };
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandMonitorDispatchTests), settings);

        // Published, but its message never reaches the dispatcher.
        var command = await worker.PublishedAsync();

        var sent = await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        Assert.Single(worker.CommandRequests, r => r.Header("Idempotency-Key") == command.Command.IdempotencyKey);
        Assert.NotNull(sent.LastAnnouncedAt);
        Assert.Equal(0, sent.Announcements);
        Assert.Equal(1, sent.Command.Attempts);
    }

    // command-dispatch: "Handling keeps failing".
    [Fact]
    public async Task Command_whose_handling_keeps_failing_parks_its_message_and_is_dead_lettered()
    {
        var broken = new ThrowingCommandSender();
        await using var worker = await VendorCloudWorker.StartAsync(
            mongo,
            rabbit,
            nameof(CommandMonitorDispatchTests),
            FastMonitor,
            services => services.AddSingleton<ICommandSender>(broken));
        var device = await new DeviceStateStore(worker.Database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), VendorCloudWorker.Owner, ThrowingCommandSender.Id, "veh_broken", DeviceType.Vehicle, Capabilities.StartStop, new DeviceAttributes(77_000, 11_000)),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            Ct);
        await new ProviderAccountStore(worker.Database, TimeProvider.System).SeedAsync(new ProviderAccount(ThrowingCommandSender.Id, VendorCloudWorker.Owner, "acct_broken"), "unused", Ct);
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), device, CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), now),
            VendorCloudWorker.Owner,
            null);
        await worker.Commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), Ct);

        var dead = await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.DeadLettered, TimeSpan.FromSeconds(60));

        Assert.Equal(CommandMonitor.DispatchKeptFailing, dead.Command.History[^1].Reason);
        Assert.Equal(3, dead.Announcements);
        Assert.True(broken.Sends >= 4, $"only {broken.Sends} sends were tried");
        var dlq = worker.Host.Services.GetRequiredService<MessagingTopology>().Dispatch.DeadLetter;
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbit.ConnectionString) }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await WorkerHost.EventuallyAsync(async () => (await channel.QueueDeclarePassiveAsync(dlq, Ct)).MessageCount, n => n >= 1, TimeSpan.FromSeconds(10));
    }

    // command-dispatch: waiting for a busy device is never punished.
    [Fact]
    public async Task Command_waiting_for_a_busy_device_is_never_dead_lettered()
    {
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandMonitorDispatchTests), FastMonitor);
        var start = await worker.PublishedAsync(CommandType.StartCharging);
        await worker.Dispatcher.DispatchAsync(start.Id, Ct);
        Assert.Equal(CommandState.Sent, (await worker.ReadAsync(start.Id)).Command.State);
        var stop = await worker.PublishedAsync(CommandType.StopCharging);

        await Task.Delay(TimeSpan.FromSeconds(8), Ct);

        var waiting = await worker.ReadAsync(stop.Id);
        Assert.Equal(CommandState.Published, waiting.Command.State);
        Assert.Equal(0, waiting.Announcements);
        Assert.Null(waiting.LastAnnouncedAt);
        Assert.DoesNotContain(worker.CommandRequests, r => r.Path.EndsWith("/stop", StringComparison.Ordinal));
    }
}
