using System.Diagnostics;
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
/// The monitor's later steps join the trace stored on the command (distributed-tracing: "A command's later steps
/// join its trace"; structured-logging: "Command logs name the command, device and provider").
/// </summary>
public sealed class CommandMonitorTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // distributed-tracing: "Resend after a timeout".
    [Fact]
    public async Task A_charger_restarted_mid_command_gives_reconcile_second_dispatch_and_confirm_spans_in_the_command_trace()
    {
        using var spans = new SpanRecorder();
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, new Dictionary<string, string> { ["Commands:ConfirmationTimeout"] = "00:00:02" });
        var traceparent = SpanRecorder.NewTraceparent();
        var stored = ActivityContext.Parse(traceparent, null);
        var command = await world.CommandAsync(CommandType.StartCharging, traceparent: traceparent);

        await world.AnswerNextCallAsync();
        await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Sent);
        await world.ReconnectAsync();
        await world.ReportAsync("Available");
        await world.AnswerNextCallAsync();
        await world.ReportAsync("Charging");
        var confirmed = await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed);

        var trace = await spans.WaitForTraceAsync(stored.TraceId, ["command reconcile", "command resend", "command confirm"]);
        var reconcile = Assert.Single(trace, a => a.DisplayName == "command reconcile");
        var resend = Assert.Single(trace, a => a.DisplayName == "command resend");
        var confirm = Assert.Single(trace, a => a.DisplayName == "command confirm");
        Assert.Equal(stored.SpanId, reconcile.ParentSpanId);
        Assert.Equal(reconcile.SpanId, resend.ParentSpanId);
        Assert.Equal(stored.SpanId, confirm.ParentSpanId);
        Assert.Equal("resend", reconcile.GetTagItem("cheaphours.outcome"));
        Assert.Equal("ocpp", reconcile.GetTagItem("cheaphours.provider.id"));
        Assert.True(trace.Count(a => a.DisplayName == "dispatch StartCharging") >= 2, "the resend is dispatched again in the same trace");
        Assert.InRange(confirmed.Command.Attempts, 2, 6);

        var resent = Assert.Single(world.WorkerLogs.Entries, e => e.Category.EndsWith(".CommandMonitor", StringComparison.Ordinal) && e.Message.Contains("resent", StringComparison.Ordinal));
        Assert.Equal(command.Id.Value, resent.Scopes["commandId"]);
        Assert.Equal(world.ChargerId.Value, resent.Scopes["deviceId"]);
        Assert.Equal("ocpp", resent.Scopes["providerId"]);
        Assert.Equal(resend.TraceId.ToString(), resent.Scopes["TraceId"]);
    }

    // distributed-tracing: "Dead-lettered command".
    [Fact]
    public async Task A_command_whose_dispatch_keeps_failing_is_dead_lettered_in_an_error_span_of_its_trace()
    {
        using var spans = new SpanRecorder();
        await using var worker = await VendorCloudWorker.StartAsync(
            mongo,
            rabbit,
            nameof(CommandMonitorTracingTests),
            new Dictionary<string, string>
            {
                ["Commands:MonitorInterval"] = "00:00:00.200",
                ["Commands:DispatchTimeout"] = "00:00:01",
                ["Messaging:FirstRetryDelay"] = "00:00:00.100",
                ["Messaging:SecondRetryDelay"] = "00:00:00.200",
                ["Messaging:ThirdRetryDelay"] = "00:00:00.300",
            },
            services => services.AddSingleton<ICommandSender>(new ThrowingCommandSender()));
        var device = await new DeviceStateStore(worker.Database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), VendorCloudWorker.Owner, ThrowingCommandSender.Id, "veh_broken", DeviceType.Vehicle, Capabilities.StartStop, new DeviceAttributes(77_000, 11_000)),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            Ct);
        await new ProviderAccountStore(worker.Database, TimeProvider.System).SeedAsync(new ProviderAccount(ThrowingCommandSender.Id, VendorCloudWorker.Owner, "acct_broken"), "unused", Ct);
        var traceparent = SpanRecorder.NewTraceparent();
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), device, CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), now),
            VendorCloudWorker.Owner,
            traceparent);
        await worker.Commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), Ct);

        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.DeadLettered, TimeSpan.FromSeconds(60));

        var trace = await spans.WaitForTraceAsync(SpanRecorder.TraceIdOf(traceparent), ["command reannounce", "command dead-letter"]);
        var deadLetter = Assert.Single(trace, a => a.DisplayName == "command dead-letter");
        Assert.Equal(ActivityStatusCode.Error, deadLetter.Status);
        Assert.Equal(CommandMonitor.DispatchKeptFailing, deadLetter.GetTagItem("cheaphours.reason"));
        Assert.Equal(ThrowingCommandSender.Id, deadLetter.GetTagItem("cheaphours.provider.id"));
        Assert.Contains(trace, a => a.DisplayName == "dispatch StartCharging" && a.Status == ActivityStatusCode.Error);

        var logged = Assert.Single(worker.Logs.Entries, e => e.Category.EndsWith(".CommandMonitor", StringComparison.Ordinal) && e.Message.Contains("dead-lettered", StringComparison.Ordinal));
        Assert.Equal(command.Id.Value, logged.Scopes["commandId"]);
        Assert.Equal(device.Value, logged.Scopes["deviceId"]);
        Assert.Equal(ThrowingCommandSender.Id, logged.Scopes["providerId"]);
    }
}
