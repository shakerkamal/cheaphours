using CheapHours.Core.Commands;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using MongoDB.Bson;
using static CheapHours.IntegrationTests.Persistence.GoalPlanSamples;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// Commands written inside the goal transactions (command-dispatch spec: saved together with the outbox; replaced
/// plan versions cancel their unsent commands). The tests share one database and keep apart by a per-test suffix.
/// </summary>
public sealed class CommandTransactionTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private GoalStore Goals => new(_database);

    private PlanStore Plans => new(_database);

    private CommandStore Commands => new(_database);

    private IMongoCollection<BsonDocument> Outbox => _database.GetCollection<BsonDocument>(MongoIndexes.Outbox);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(CommandTransactionTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // command-dispatch: "Write fails part-way".
    [Fact]
    public async Task Confirmation_that_fails_part_way_leaves_no_command_no_outbox_entry_and_no_plan_change()
    {
        var goal = await NewGoalAsync("fail");
        var draft = Version(goal, 1);
        var batch = CommandSamples.ForPlan(draft, goal.Goal.ChargerDeviceId, Now);

        // A command elsewhere already holds the second command's key, so the insert fails after the first one.
        var squatter = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.Parse($"cmd_squat{_s}"), goal.Goal.ChargerDeviceId, CommandType.StopCharging, batch.Commands[1].Command.IdempotencyKey), Now),
            Owner,
            null);
        await Commands.CreateWithOutboxAsync(squatter, CommandSamples.Announcement(squatter), Ct);

        await Assert.ThrowsAnyAsync<MongoException>(() => Goals.ConfirmAsync(draft, null, null, batch, Now, Ct));

        Assert.Empty(await Commands.ListForPlanVersionAsync(goal.PlanId, 1, Ct));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("commandId", batch.Commands.Select(c => c.Id.Value)), cancellationToken: Ct));
        var plan = await Plans.GetCurrentAsync(goal.Goal.Id, Ct);
        Assert.Equal(PlanStatus.Draft, plan!.Plan.Status);
        Assert.Null(plan.ConfirmedAt);
    }

    // command-dispatch: "New prices before the start".
    [Fact]
    public async Task New_version_cancels_the_pending_start_and_its_unpublished_announcement()
    {
        var (goal, running, v1) = await ConfirmedAsync("np");
        var start = v1.Commands[0];
        var next = Version(goal, 2, PlanStatus.Confirmed, confirmedAt: Now, reason: PlanReason.PricesPublished);
        var v2 = CommandSamples.ForPlan(next, goal.Goal.ChargerDeviceId, Now.AddMinutes(1));

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ReplanConfirmedAsync(next, running, v2, Now.AddMinutes(1), Ct));

        var cancelled = await Commands.FindAsync(start.Id, Ct);
        Assert.Equal(CommandState.Cancelled, cancelled!.Command.State);
        Assert.Equal($"Superseded by {goal.PlanId}:v2", cancelled.Command.History[^1].Reason);
        Assert.Equal(0, await Outbox.CountDocumentsAsync(new BsonDocument("commandId", start.Id.Value), cancellationToken: Ct));
        Assert.Equal(CommandState.Pending, (await Commands.FindAsync(v2.Commands[0].Id, Ct))!.Command.State);
    }

    // command-dispatch: "Start already sent".
    [Fact]
    public async Task Start_already_sent_stays_sent_when_a_new_version_replaces_it()
    {
        var (goal, running, v1) = await ConfirmedAsync("sent");
        var start = await MoveAsync(v1.Commands[0], c => { c.Publish(Now.AddSeconds(1)); c.Accept(Now.AddSeconds(2), "ocpp-1"); });

        await Goals.ReplanConfirmedAsync(Version(goal, 2, PlanStatus.Confirmed, reason: PlanReason.PricesPublished), running, CommandBatch.None, Now.AddMinutes(1), Ct);

        Assert.Equal(CommandState.Sent, (await Commands.FindAsync(start.Id, Ct))!.Command.State);
    }

    [Fact]
    public async Task Claimed_command_is_left_to_finish_and_an_unclaimed_published_one_is_cancelled()
    {
        var (goal, running, v1) = await ConfirmedAsync("claim");
        var claimed = await MoveAsync(v1.Commands[0], c => c.Publish(Now.AddSeconds(1)));
        var unclaimed = await MoveAsync(v1.Commands[1], c => c.Publish(Now.AddSeconds(1)));
        var due = claimed.Command.DueAt;
        Assert.NotNull(await Commands.TryClaimAsync(claimed.Id, "worker-0", due, TimeSpan.FromSeconds(45), Ct));

        await Goals.ReplanConfirmedAsync(Version(goal, 2, PlanStatus.Confirmed, reason: PlanReason.ChargeDrift), running, CommandBatch.None, due.AddSeconds(1), Ct);

        Assert.Equal(CommandState.Published, (await Commands.FindAsync(claimed.Id, Ct))!.Command.State);
        Assert.Equal(CommandState.Cancelled, (await Commands.FindAsync(unclaimed.Id, Ct))!.Command.State);
    }

    [Fact]
    public async Task Cancelling_keeps_announcements_that_were_already_published()
    {
        var (goal, running, v1) = await ConfirmedAsync("pub");
        var start = v1.Commands[0];
        await new CheapHours.Persistence.Outbox.OutboxStore(_database).MarkPublishedAsync(v1.Announcements[0].MessageId, Now, Ct);

        await Goals.ReplanConfirmedAsync(Version(goal, 2, PlanStatus.Confirmed, reason: PlanReason.PricesPublished), running, CommandBatch.None, Now.AddMinutes(1), Ct);

        Assert.Equal(CommandState.Cancelled, (await Commands.FindAsync(start.Id, Ct))!.Command.State);
        Assert.Equal(1, await Outbox.CountDocumentsAsync(new BsonDocument("commandId", start.Id.Value), cancellationToken: Ct));
    }

    // charging-goals: "Change draft confirmed".
    [Fact]
    public async Task Confirming_a_change_draft_cancels_the_old_versions_unsent_commands()
    {
        var (goal, running, v1) = await ConfirmedAsync("chg");
        var draft = Version(goal, 2) with { ProposedTargetSocPercent = 90 };
        await Goals.AddChangeDraftAsync(draft, Ct);
        var changed = goal with { Goal = new Goal(goal.Goal.Id, Owner, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, 90, goal.Goal.DepartureUtc) };

        await Goals.ConfirmAsync(draft, running, changed, CommandSamples.ForPlan(draft, goal.Goal.ChargerDeviceId, Now.AddMinutes(1)), Now.AddMinutes(1), Ct);

        Assert.All(await Commands.ListForPlanVersionAsync(goal.PlanId, 1, Ct), c => Assert.Equal(CommandState.Cancelled, c.Command.State));
        Assert.All(await Commands.ListForPlanVersionAsync(goal.PlanId, 2, Ct), c => Assert.Equal(CommandState.Pending, c.Command.State));
        Assert.Equal(90, (await Goals.GetAsync(Owner, goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
    }

    private async Task<StoredGoal> NewGoalAsync(string name)
    {
        var goal = Goal($"goal_{name}{_s}", $"dev_car{name}{_s}", $"dev_box{name}{_s}", Now.AddHours(13));
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct));
        return goal;
    }

    private async Task<(StoredGoal Goal, StoredPlan Running, CommandBatch Commands)> ConfirmedAsync(string name)
    {
        var goal = await NewGoalAsync(name);
        var draft = Version(goal, 1);
        var batch = CommandSamples.ForPlan(draft, goal.Goal.ChargerDeviceId, Now);
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ConfirmAsync(draft, null, null, batch, Now, Ct));
        return (goal, (await Plans.GetRunningAsync(goal.Goal.Id, Ct))!, batch);
    }

    private async Task<StoredCommand> MoveAsync(StoredCommand command, Action<Command> move)
    {
        var loaded = (await Commands.FindAsync(command.Id, Ct))!;
        move(loaded.Command);
        return (await Commands.SaveAsync(loaded, Ct))!;
    }
}
