using System.Diagnostics.Metrics;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// Time to confirmation and dead-lettered commands, recorded once per stored change (operational-metrics: "Time to
/// confirmation is measured", "Dead-lettered commands are counted").
/// </summary>
public sealed class CommandMetricsTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // operational-metrics: "Confirmed seven seconds after it was due".
    [Fact]
    public void Confirmation_time_is_measured_from_when_the_command_was_due()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();
        using var recorder = new MetricRecorder(meters, CommandMetrics.ConfirmationDurationName);
        var metrics = new CommandMetrics(meters);
        var due = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

        metrics.Confirmed(new StoredCommand(SentCommand(due), Owner, null), "ocpp");
        metrics.Confirmed(null, "ocpp");
        var confirmed = SentCommand(due);
        confirmed.Confirm(due.AddSeconds(7));
        metrics.Confirmed(new StoredCommand(confirmed, Owner, null), "ocpp");

        var measurement = Assert.Single(recorder.All);
        Assert.Equal(7, measurement.Value);
        Assert.Equal(new Dictionary<string, string?> { ["provider.id"] = "ocpp", ["command.type"] = "StartCharging" }, measurement.Tags);
    }

    // operational-metrics: "Duplicate confirmation".
    [Fact]
    public async Task A_confirmed_command_is_measured_once_and_a_second_matching_report_adds_nothing()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit);
        using var recorder = new MetricRecorder(world.Worker.Services.GetRequiredService<IMeterFactory>(), CommandMetrics.ConfirmationDurationName);
        var command = await world.CommandAsync(CommandType.StartCharging);

        await world.AnswerNextCallAsync();
        await world.ReportAsync("Charging");
        await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed);
        await world.ReportAsync("Charging");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        var measurement = Assert.Single(recorder.All);
        Assert.True(measurement.Has(("provider.id", "ocpp"), ("command.type", "StartCharging")));
        Assert.InRange(measurement.Value, 0, 30);
    }

    // operational-metrics: "Sixth transient failure".
    [Fact]
    public async Task The_sixth_transient_send_dead_letters_the_command_as_attempts_exhausted()
    {
        await using var worker = await VendorCloudWorker.StartAsync(
            mongo,
            rabbit,
            nameof(CommandMetricsTests),
            new Dictionary<string, string> { ["Commands:BaseDelay"] = "00:00:00.010", ["Commands:MaxDelay"] = "00:00:00.040" });
        using var recorder = new MetricRecorder(worker.Host.Services.GetRequiredService<IMeterFactory>(), CommandMetrics.DeadLetteredName);
        var command = await worker.PublishedAsync();
        worker.Vendor.FailWith(429);

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(60, Ct);
            await worker.Dispatcher.DispatchAsync(command.Id, Ct);
        }

        Assert.Equal(CommandState.DeadLettered, (await worker.ReadAsync(command.Id)).Command.State);
        var measurement = Assert.Single(recorder.All);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(new Dictionary<string, string?> { ["provider.id"] = "carcloud", ["command.type"] = "StartCharging", ["reason"] = CommandMetrics.AttemptsExhausted }, measurement.Tags);
    }

    // operational-metrics: "Dispatch kept failing".
    [Fact]
    public async Task The_monitor_dead_letters_a_command_whose_dispatch_kept_failing_as_dispatch_kept_failing()
    {
        await using var worker = await VendorCloudWorker.StartAsync(
            mongo,
            rabbit,
            nameof(CommandMetricsTests),
            new Dictionary<string, string>
            {
                ["Commands:MonitorInterval"] = "00:00:00.200",
                ["Commands:DispatchTimeout"] = "00:00:01",
                ["Messaging:FirstRetryDelay"] = "00:00:00.100",
                ["Messaging:SecondRetryDelay"] = "00:00:00.200",
                ["Messaging:ThirdRetryDelay"] = "00:00:00.300",
            },
            services => services.AddSingleton<ICommandSender>(new ThrowingCommandSender()));
        using var recorder = new MetricRecorder(worker.Host.Services.GetRequiredService<IMeterFactory>(), CommandMetrics.DeadLetteredName);
        var device = await new DeviceStateStore(worker.Database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), VendorCloudWorker.Owner, ThrowingCommandSender.Id, "veh_broken", DeviceType.Vehicle, Capabilities.StartStop, new DeviceAttributes(77_000, 11_000)),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            Ct);
        await new ProviderAccountStore(worker.Database, TimeProvider.System).SeedAsync(new ProviderAccount(ThrowingCommandSender.Id, VendorCloudWorker.Owner, "acct_broken"), "unused", Ct);
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), device, CommandType.StopCharging, $"key_{Guid.NewGuid():N}"), now),
            VendorCloudWorker.Owner,
            null);
        await worker.Commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), Ct);

        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.DeadLettered, TimeSpan.FromSeconds(60));

        var measurement = Assert.Single(await recorder.WaitForAsync(CommandMetrics.DeadLetteredName));
        Assert.Equal(new Dictionary<string, string?> { ["provider.id"] = ThrowingCommandSender.Id, ["command.type"] = "StopCharging", ["reason"] = CommandMetrics.DispatchKeptFailing }, measurement.Tags);
    }

    // Created three hours before it is due (a planned command), then sent two seconds after it became due.
    private static Command SentCommand(DateTimeOffset due)
    {
        var command = Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), DeviceId.Parse("dev_cp001"), CommandType.StartCharging, "key_metrics"), due.AddHours(-3), due);
        command.Publish(due);
        command.Accept(due.AddSeconds(2), "ref-1");
        return command;
    }
}
