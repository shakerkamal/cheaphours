using CheapHours.Core.Commands;
using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// Confirmation and reconcile on the whole OCPP path: worker, gateway and a test charger (command-dispatch spec: the
/// device confirms a command; unconfirmed commands are reconciled against the device).
/// </summary>
public sealed class CommandMonitorConfirmTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    // command-dispatch: "Start confirmed by the charger" and ocpp-gateway "Charger accepts".
    [Fact]
    public async Task Charging_reported_after_the_send_confirms_the_start()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit);
        var command = await world.CommandAsync(CommandType.StartCharging);

        var call = await world.AnswerNextCallAsync();
        await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Sent);
        await world.ReportAsync("Charging");

        var confirmed = await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed);
        Assert.Equal("RemoteStartTransaction", call.Action);
        Assert.Equal(1, confirmed.Command.Attempts);
        Assert.Equal([CommandState.Pending, CommandState.Published, CommandState.Sent, CommandState.Confirmed], confirmed.Command.History.Select(h => h.State));
    }

    // Like the pinned simulator: the charger accepts the remote start and reports Charging at once, before the
    // worker has applied the gateway's result. The report still counts as after the send.
    [Fact]
    public async Task Charging_reported_right_after_accepting_confirms_without_waiting_for_the_timeout()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit);
        var command = await world.CommandAsync(CommandType.StartCharging);

        await world.AnswerNextCallAsync();
        await world.ReportAsync("Charging");

        var confirmed = await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed, seconds: 15);
        Assert.Equal(1, confirmed.Command.Attempts);
    }

    // command-dispatch: "Report from before the send".
    [Fact]
    public async Task Charging_reported_before_the_send_does_not_confirm()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit);
        await world.ReportAsync("Charging");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var command = await world.CommandAsync(CommandType.StartCharging);

        await world.AnswerNextCallAsync();
        await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Sent);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(CommandState.Sent, (await world.ReadAsync(command.Id)).Command.State);
    }

    // command-dispatch: "Confirmation was only late" (reconcile confirms without a resend).
    [Fact]
    public async Task Late_confirmation_is_confirmed_by_the_reconcile_without_a_resend()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, new Dictionary<string, string> { ["Commands:ConfirmationTimeout"] = "00:00:02" });
        var command = await world.CommandAsync(CommandType.StopCharging);

        // A stop for an idle charger is already in effect; the charger is Available, so it confirms.
        await world.ReportAsync("Available");
        var confirmed = await world.EventuallyAsync(command.Id, c => c.Command.State == CommandState.Confirmed);

        Assert.Equal(1, confirmed.Command.Attempts);
        Assert.Equal("already-in-effect", confirmed.Command.History.Single(h => h.State == CommandState.Sent).VendorReference);
    }

    // command-dispatch: unconfirmed commands are reconciled; the resend keeps the key.
    [Fact]
    public async Task Silent_charger_gets_the_start_again_with_the_same_key_after_the_timeout()
    {
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, new Dictionary<string, string> { ["Commands:ConfirmationTimeout"] = "00:00:02" });
        var command = await world.CommandAsync(CommandType.StartCharging);

        var first = await world.AnswerNextCallAsync();
        var second = await world.AnswerNextCallAsync();

        var resent = await world.EventuallyAsync(command.Id, c => c.Command.Attempts == 2 && c.Command.State == CommandState.Sent);
        Assert.Equal(("RemoteStartTransaction", "RemoteStartTransaction"), (first.Action, second.Action));
        Assert.Equal(command.Command.IdempotencyKey, resent.Command.IdempotencyKey);
        var states = resent.Command.History.Select(h => h.State).ToArray();
        Assert.Equal([CommandState.Pending, CommandState.Published, CommandState.Sent, CommandState.Reconciling, CommandState.Published, CommandState.Sent], states);
    }
}
