using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;

namespace CheapHours.IntegrationTests.Faults;

/// <summary>
/// The gate of command dispatch: killing the worker or the broker mid-command loses no command and sends none twice,
/// and a charger that drops mid-command charges exactly once (command-dispatch spec; reliability table rows "worker
/// crashes between saving and publishing", "RabbitMQ is down" and "charger drops the connection mid-command").
/// </summary>
public sealed class CommandFaultTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // command-dispatch: "Worker stops between saving and publishing".
    [Fact]
    public async Task Command_saved_while_no_worker_runs_reaches_its_device_exactly_once_when_a_worker_starts()
    {
        StoredCommand? saved = null;
        await using var worker = await VendorCloudWorker.StartAsync(
            mongo,
            rabbit,
            nameof(CommandFaultTests),
            beforeStart: async (database, vehicle) => saved = await SaveAsync(database, vehicle));

        var sent = await WorkerHost.EventuallyAsync(() => worker.ReadAsync(saved!.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Single(worker.CommandRequests, r => r.Header("Idempotency-Key") == saved!.Command.IdempotencyKey);
        Assert.Equal(1, sent.Command.Attempts);
    }

    // command-dispatch: "Broker stopped".
    [Fact]
    public async Task Three_commands_accepted_while_the_broker_is_down_each_reach_their_device_once()
    {
        await using var broker = await RabbitMqFixture.StartDedicatedAsync();
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandFaultTests), rabbitConnectionString: broker.ConnectionString);
        var vehicles = new[] { worker.Vehicle, await VendorCloudWorker.AddVehicleAsync(worker.Database, "veh_two"), await VendorCloudWorker.AddVehicleAsync(worker.Database, "veh_three") };

        await broker.StopAsync();

        // Accepting a command needs only MongoDB: the same path the API's 202 takes.
        var service = new CommandService(new DeviceStateStore(worker.Database), worker.Commands, TimeProvider.System);
        var accepted = new List<StoredCommand>();
        foreach (var vehicle in vehicles)
        {
            var outcome = await service.CreateAsync(VendorCloudWorker.Owner, vehicle, CommandType.StartCharging, null, null, Ct);
            accepted.Add(Assert.IsType<CommandRequestOutcome.Accepted>(outcome).Command);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Empty(worker.CommandRequests);

        await broker.StartAsync();
        foreach (var command in accepted)
        {
            await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(90));
        }

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.All(accepted, c => Assert.Single(worker.CommandRequests, r => r.Header("Idempotency-Key") == c.Command.IdempotencyKey));
        Assert.Equal(3, worker.CommandRequests.Count);
        Assert.Equal(3, worker.CommandRequests.Select(r => r.Path).Distinct(StringComparer.Ordinal).Count());
    }

    // command-dispatch: "Charger restarted mid-command".
    [Fact]
    public async Task Charger_dropping_mid_command_and_reconnecting_available_charges_exactly_once()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, new Dictionary<string, string> { ["Commands:ConfirmationTimeout"] = "00:00:02" });
        var command = await world.CommandAsync(CommandType.StartCharging);

        // The charger accepts the remote start, then drops before it starts charging.
        var first = await world.AnswerNextCallAsync();
        await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Sent);
        await world.ReconnectAsync();
        await world.ReportAsync("Available");

        // The monitor reconciles against the device, which is not charging, and resends with the same key.
        var second = await world.AnswerNextCallAsync();
        var started = await world.Charger.CallAsync($$"""[2,"st1","StartTransaction",{"connectorId":1,"idTag":"CHEAPHOURS","meterStart":1000,"timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}"}]""");
        await world.ReportAsync("Charging");

        var confirmed = await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed);
        Assert.Equal(("RemoteStartTransaction", "RemoteStartTransaction"), (first.Action, second.Action));
        // A resend that met the closing connection is transient and retried, so it can take more than one attempt;
        // only two starts ever reached the charger.
        Assert.InRange(confirmed.Command.Attempts, 2, 6);
        Assert.Equal(command.Command.IdempotencyKey, confirmed.Command.IdempotencyKey);
        var session = (await new DeviceStateStore(world.Database).GetAsync(world.ChargerId, Ct))!.ActiveSessionId;
        Assert.Equal(started[2]!["transactionId"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture), session);

        // Nothing more reaches the charger: the next frame answers a heartbeat, not a third start.
        var next = await world.Charger.CallAsync("""[2,"probe","Heartbeat",{}]""");
        Assert.Equal("probe", next[1]!.GetValue<string>());
    }

    private static async Task<StoredCommand> SaveAsync(IMongoDatabase database, DeviceId vehicle)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), vehicle, CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), now),
            VendorCloudWorker.Owner,
            null);
        Assert.True(await new CommandStore(database).CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), Ct));
        return command;
    }
}
