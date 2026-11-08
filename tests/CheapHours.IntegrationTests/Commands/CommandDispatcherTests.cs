using CheapHours.Core.Commands;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// The dispatcher on a real worker against the car-cloud provider and a WireMock vendor fake (command-dispatch
/// spec: repeated messages act once, one command at a time per device, the send outcome moves the command).
/// </summary>
public sealed class CommandDispatcherTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private VendorCloudWorker _worker = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Tiny retry delays, so a test can run all six attempts.
    public async ValueTask InitializeAsync() => _worker = await VendorCloudWorker.StartAsync(
        mongo,
        rabbit,
        nameof(CommandDispatcherTests),
        new Dictionary<string, string> { ["Commands:BaseDelay"] = "00:00:00.010", ["Commands:MaxDelay"] = "00:00:00.040" });

    public async ValueTask DisposeAsync() => await _worker.DisposeAsync();

    // command-dispatch: "Request delivered twice". The relay and the consumer send the command once; the same
    // request delivered again, and three concurrent dispatches of another command, send nothing more.
    [Fact]
    public async Task Same_request_delivered_twice_or_concurrently_sends_once()
    {
        var command = await _worker.PendingAsync();
        await WorkerHost.EventuallyAsync(() => _worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        var announcement = CommandOutbox.Announce(command, DateTimeOffset.UtcNow);
        var publisher = _worker.Host.Services.GetRequiredService<IMessagePublisher>();

        await publisher.PublishSerializedAsync(CommandOutbox.ToMessage(announcement), announcement.Destination, toQueue: false, Ct);
        await publisher.PublishSerializedAsync(CommandOutbox.ToMessage(announcement), announcement.Destination, toQueue: false, Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Single(_worker.CommandRequests, r => r.Header("Idempotency-Key") == command.Command.IdempotencyKey);
        var first = await _worker.ReadAsync(command.Id);
        Assert.Equal((CommandState.Sent, 1), Summary(first));
        Assert.Single(first.Command.History, h => h.State == CommandState.Sent);

        // The device confirms the first command, so the next one for it may go.
        first.Command.Confirm(DateTimeOffset.UtcNow);
        await _worker.Commands.SaveAsync(first, Ct);
        var concurrent = await _worker.PublishedAsync();
        await Task.WhenAll(_worker.Dispatcher.DispatchAsync(concurrent.Id, Ct), _worker.Dispatcher.DispatchAsync(concurrent.Id, Ct), _worker.Dispatcher.DispatchAsync(concurrent.Id, Ct));

        Assert.Single(_worker.CommandRequests, r => r.Header("Idempotency-Key") == concurrent.Command.IdempotencyKey);
        Assert.Equal((CommandState.Sent, 1), Summary(await _worker.ReadAsync(concurrent.Id)));
    }

    // command-dispatch: "Stop waits for the start".
    [Fact]
    public async Task Stop_waits_until_the_start_is_confirmed()
    {
        var start = await _worker.PublishedAsync(CommandType.StartCharging);
        await _worker.Dispatcher.DispatchAsync(start.Id, Ct);
        var stop = await _worker.PublishedAsync(CommandType.StopCharging);

        await _worker.Dispatcher.DispatchAsync(stop.Id, Ct);

        Assert.Equal(CommandState.Published, (await _worker.ReadAsync(stop.Id)).Command.State);
        Assert.DoesNotContain(_worker.CommandRequests, r => r.Path.EndsWith("/stop", StringComparison.Ordinal));

        var sent = await _worker.ReadAsync(start.Id);
        sent.Command.Confirm(DateTimeOffset.UtcNow);
        await _worker.Commands.SaveAsync(sent, Ct);
        await _worker.Dispatcher.DispatchAsync(stop.Id, Ct);

        Assert.Equal(CommandState.Sent, (await _worker.ReadAsync(stop.Id)).Command.State);
        Assert.Single(_worker.CommandRequests, r => r.Path.EndsWith("/stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Later_command_waits_for_an_earlier_due_one_of_the_same_device()
    {
        var earlier = await _worker.PublishedAsync(CommandType.StartCharging, DateTimeOffset.UtcNow.AddSeconds(-30));
        var later = await _worker.PublishedAsync(CommandType.StopCharging, DateTimeOffset.UtcNow.AddSeconds(-10));

        await _worker.Dispatcher.DispatchAsync(later.Id, Ct);

        Assert.Equal(CommandState.Published, (await _worker.ReadAsync(later.Id)).Command.State);
        Assert.Empty(_worker.CommandRequests);
        _ = earlier;
    }

    [Fact]
    public async Task Command_that_is_not_due_is_not_sent()
    {
        var later = await _worker.PublishedAsync(dueAt: DateTimeOffset.UtcNow.AddHours(1));

        await _worker.Dispatcher.DispatchAsync(later.Id, Ct);

        Assert.Equal((CommandState.Published, 0), Summary(await _worker.ReadAsync(later.Id)));
        Assert.Empty(_worker.CommandRequests);
    }

    // command-dispatch: "Rate-limited".
    [Fact]
    public async Task Rate_limit_with_retry_after_schedules_the_next_attempt_no_sooner()
    {
        var command = await _worker.PublishedAsync();
        _worker.Vendor.FailWith(429, TimeSpan.FromSeconds(30));
        var before = DateTimeOffset.UtcNow;

        await _worker.Dispatcher.DispatchAsync(command.Id, Ct);

        var read = await _worker.ReadAsync(command.Id);
        Assert.Equal((CommandState.Published, 1), Summary(read));
        Assert.True(read.Command.NextAttemptAt >= before.AddSeconds(30), $"next attempt {read.Command.NextAttemptAt:O}");
        Assert.Null(read.Claim);

        // Not before its time: a dispatch now sends nothing.
        await _worker.Dispatcher.DispatchAsync(command.Id, Ct);
        Assert.Equal(1, (await _worker.ReadAsync(command.Id)).Command.Attempts);
    }

    // command-dispatch: "Out of attempts".
    [Fact]
    public async Task Sixth_transient_failure_dead_letters_with_the_last_failure_as_reason()
    {
        var command = await _worker.PublishedAsync();
        _worker.Vendor.FailWith(429);

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(60, Ct);
            await _worker.Dispatcher.DispatchAsync(command.Id, Ct);
        }

        var read = await _worker.ReadAsync(command.Id);
        Assert.Equal((CommandState.DeadLettered, 6), Summary(read));
        Assert.False(string.IsNullOrWhiteSpace(read.Command.History[^1].Reason));
        Assert.Equal(read.Command.History[^2].Reason, read.Command.History[^1].Reason);
    }

    [Fact]
    public async Task Vendor_refusal_rejects_the_command()
    {
        var command = await _worker.PublishedAsync();
        _worker.Vendor.FailWith(422, body: """{"error":"not_plugged_in"}""");

        await _worker.Dispatcher.DispatchAsync(command.Id, Ct);

        var read = await _worker.ReadAsync(command.Id);
        Assert.Equal((CommandState.Rejected, 1), Summary(read));
        Assert.False(string.IsNullOrWhiteSpace(read.Command.History[^1].Reason));
    }

    private static (CommandState State, int Attempts) Summary(CheapHours.Persistence.Commands.StoredCommand command) =>
        (command.Command.State, command.Command.Attempts);
}
