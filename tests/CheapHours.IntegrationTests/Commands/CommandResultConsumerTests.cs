using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// Gateway results on <c>worker.command-results</c>, published as a gateway replica would (command-dispatch spec:
/// the send outcome moves the command; repeated messages act once).
/// </summary>
public sealed class CommandResultConsumerTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private VendorCloudWorker _worker = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMessagePublisher Publisher => _worker.Host.Services.GetRequiredService<IMessagePublisher>();

    public async ValueTask InitializeAsync() => _worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandResultConsumerTests));

    public async ValueTask DisposeAsync() => await _worker.DisposeAsync();

    // command-dispatch: "Accepted".
    [Fact]
    public async Task Accepted_result_makes_the_command_sent_with_the_vendor_reference()
    {
        var command = await _worker.PublishedAsync();

        await Publisher.PublishAsync(Result(command.Id, new SendResult.Accepted("ocpp-4471")), Ct);

        var sent = await WorkerHost.EventuallyAsync(() => _worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, Within);
        Assert.Equal("ocpp-4471", sent.Command.History[^1].VendorReference);
        Assert.Equal(1, sent.Command.Attempts);
    }

    // command-dispatch: "Result delivered twice".
    [Fact]
    public async Task Duplicate_result_records_one_sent_entry()
    {
        var command = await _worker.PublishedAsync();
        var result = Result(command.Id, new SendResult.Accepted("ocpp-1"));

        await Publisher.PublishAsync(result, Ct);
        await Publisher.PublishAsync(result, Ct);
        await WorkerHost.EventuallyAsync(() => _worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, Within);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        var read = await _worker.ReadAsync(command.Id);
        Assert.Single(read.Command.History, h => h.State == CommandState.Sent);
        Assert.Equal(1, read.Command.Attempts);
    }

    [Fact]
    public async Task Result_for_a_cancelled_command_is_dropped()
    {
        var command = await _worker.PublishedAsync();
        var loaded = await _worker.ReadAsync(command.Id);
        loaded.Command.Cancel(DateTimeOffset.UtcNow, "Superseded by plan_x:v2");
        await _worker.Commands.SaveAsync(loaded, Ct);
        var marker = await _worker.PublishedAsync();

        await Publisher.PublishAsync(Result(command.Id, new SendResult.Accepted("late")), Ct);
        await Publisher.PublishAsync(Result(marker.Id, new SendResult.Accepted("marker")), Ct);
        await WorkerHost.EventuallyAsync(() => _worker.ReadAsync(marker.Id), c => c.Command.State == CommandState.Sent, Within);

        var read = await _worker.ReadAsync(command.Id);
        Assert.Equal(CommandState.Cancelled, read.Command.State);
        Assert.Equal(0, read.Command.Attempts);
    }

    [Fact]
    public async Task Transient_result_schedules_a_retry_and_clears_the_claim()
    {
        var command = await _worker.PublishedAsync();
        Assert.NotNull(await _worker.Commands.TryClaimAsync(command.Id, "worker-x", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(45), Ct));

        await Publisher.PublishAsync(Result(command.Id, new SendResult.Transient("call timed out")), Ct);

        var read = await WorkerHost.EventuallyAsync(() => _worker.ReadAsync(command.Id), c => c.Command.Attempts == 1, Within);
        Assert.Equal(CommandState.Published, read.Command.State);
        Assert.NotNull(read.Command.NextAttemptAt);
        Assert.Null(read.Claim);
    }

    private static GatewayCommandResult Result(CommandId commandId, SendResult outcome) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, null, commandId, outcome);
}
