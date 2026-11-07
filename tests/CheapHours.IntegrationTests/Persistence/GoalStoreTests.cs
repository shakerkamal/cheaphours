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
/// Goals, device claims and their transactional writes (charging-goals spec, design §3). The tests share one
/// database and keep apart by a per-test suffix on every goal and device id.
/// </summary>
public sealed class GoalStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private GoalStore Goals => new(_database);

    private PlanStore Plans => new(_database);

    private IMongoCollection<BsonDocument> Claims => _database.GetCollection<BsonDocument>(MongoIndexes.GoalClaims);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(GoalStoreTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Creating_a_goal_stores_it_with_plan_version_one()
    {
        var goal = NewGoal("a", "car", "box", Now.AddHours(13));

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct));

        Assert.Equal(goal, await Goals.GetAsync(Owner, goal.Goal.Id, Ct));
        var plan = await Plans.GetCurrentAsync(goal.Goal.Id, Ct);
        Assert.Equal(1, plan!.Plan.Version);
        Assert.Equal($"plan_a{_s}:v1", plan.VersionId);
        Assert.Equal(PlanStatus.Draft, plan.Plan.Status);
        Assert.Equal(Now.AddMinutes(10), plan.Plan.ExpiresAtUtc);
        Assert.Equal(PlanReason.Created, plan.Reason);
        Assert.Equal(Version(goal, 1).Plan.Slots, plan.Plan.Slots);
    }

    [Fact]
    public async Task Second_goal_for_a_claimed_vehicle_is_refused_and_writes_nothing()
    {
        var first = NewGoal("a", "car", "box", Now.AddHours(13));
        await Goals.CreateAsync(first, Version(first, 1), Now, Ct);
        var second = NewGoal("b", "car", "box2", Now.AddHours(20));

        Assert.Equal(GoalWriteOutcome.DeviceBusy, await Goals.CreateAsync(second, Version(second, 1), Now, Ct));

        Assert.Null(await Goals.GetAsync(Owner, second.Goal.Id, Ct));
        Assert.Null(await Plans.GetCurrentAsync(second.Goal.Id, Ct));
        Assert.Null(await Claims.Find(new BsonDocument("_id", $"dev_box2{_s}")).FirstOrDefaultAsync(Ct));
        Assert.Equal($"goal_a{_s}", (await Claims.Find(new BsonDocument("_id", $"dev_car{_s}")).SingleAsync(Ct))["goalId"].AsString);
    }

    [Fact]
    public async Task Second_goal_for_a_claimed_charger_is_refused()
    {
        var first = NewGoal("a", "car", "box", Now.AddHours(13));
        await Goals.CreateAsync(first, Version(first, 1), Now, Ct);
        var second = NewGoal("b", "car2", "box", Now.AddHours(20));

        Assert.Equal(GoalWriteOutcome.DeviceBusy, await Goals.CreateAsync(second, Version(second, 1), Now, Ct));
    }

    [Fact]
    public async Task Claim_is_free_again_after_departure()
    {
        var first = NewGoal("a", "car", "box", Now.AddHours(1));
        await Goals.CreateAsync(first, Version(first, 1), Now, Ct);
        var later = Now.AddHours(2);
        var second = NewGoal("b", "car", "box", later.AddHours(12));

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.CreateAsync(second, Version(second, 1, madeAt: later), later, Ct));

        Assert.Equal($"goal_b{_s}", (await Claims.Find(new BsonDocument("_id", $"dev_car{_s}")).SingleAsync(Ct))["goalId"].AsString);
    }

    [Fact]
    public async Task Concurrent_goals_for_one_vehicle_store_exactly_one()
    {
        var goals = Enumerable.Range(0, 4).Select(i => NewGoal($"c{i}", "car", $"box{i}", Now.AddHours(13))).ToList();

        var outcomes = await Task.WhenAll(goals.Select(g => Goals.CreateAsync(g, Version(g, 1), Now, Ct)));

        Assert.Single(outcomes, o => o == GoalWriteOutcome.Stored);
        var ids = new BsonArray(goals.Select(g => g.Goal.Id.Value));
        Assert.Equal(1, await _database.GetCollection<BsonDocument>(MongoIndexes.Goals).CountDocumentsAsync(new BsonDocument("_id", new BsonDocument("$in", ids)), cancellationToken: Ct));
    }

    [Fact]
    public async Task Changing_a_goal_stores_it_with_a_new_version_and_moves_the_claims()
    {
        var goal = NewGoal("a", "car", "box", Now.AddHours(13));
        await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct);
        var changedAt = Now.AddMinutes(5);
        var changed = goal with { Goal = new Goal(goal.Goal.Id, Owner, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, 90, Now.AddHours(15)), UpdatedAt = changedAt };

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ChangeAsync(changed, Version(changed, 2, madeAt: changedAt, reason: PlanReason.GoalChanged), Ct));

        var stored = await Goals.GetAsync(Owner, goal.Goal.Id, Ct);
        Assert.Equal(90, stored!.Goal.TargetSocPercent);
        Assert.Equal(Now.AddHours(15), stored.Goal.DepartureUtc);
        Assert.Equal(changedAt, stored.UpdatedAt);
        var versions = await Plans.ListVersionsAsync(goal.Goal.Id, Ct);
        Assert.Equal([PlanStatus.Superseded, PlanStatus.Draft], versions.Select(v => v.Plan.Status));
        Assert.Equal(PlanReason.GoalChanged, versions[1].Reason);
        var claims = await Claims.Find(new BsonDocument("goalId", goal.Goal.Id.Value)).ToListAsync(Ct);
        Assert.Equal(2, claims.Count);
        Assert.All(claims, c => Assert.Equal(Now.AddHours(15).UtcDateTime, c["until"].ToUniversalTime()));
    }

    [Fact]
    public async Task Change_racing_another_version_writes_nothing()
    {
        var goal = NewGoal("a", "car", "box", Now.AddHours(13));
        await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct);
        Assert.True(await Plans.AddVersionAsync(Version(goal, 2, reason: PlanReason.PricesPublished), Ct));
        var changed = goal with { Goal = new Goal(goal.Goal.Id, Owner, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, 90, goal.Goal.DepartureUtc) };

        Assert.Equal(GoalWriteOutcome.VersionTaken, await Goals.ChangeAsync(changed, Version(changed, 2, reason: PlanReason.GoalChanged), Ct));

        Assert.Equal(80, (await Goals.GetAsync(Owner, goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
    }

    [Fact]
    public async Task Another_owners_goal_is_not_found()
    {
        var goal = NewGoal("a", "car", "box", Now.AddHours(13));
        await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct);

        Assert.Null(await Goals.GetAsync(OwnerId.Parse("own_other"), goal.Goal.Id, Ct));
    }

    [Fact]
    public async Task Open_and_departing_lists_filter_by_departure()
    {
        var soon = NewGoal("a", "car", "box", Now.AddHours(2));
        var later = NewGoal("b", "car2", "box2", Now.AddHours(20));
        var other = NewGoal("c", "car3", "box3", Now.AddHours(5), OwnerId.Parse("own_other"));
        foreach (var g in new[] { soon, later, other })
        {
            await Goals.CreateAsync(g, Version(g, 1), Now, Ct);
        }

        Assert.Equal([$"goal_a{_s}", $"goal_c{_s}", $"goal_b{_s}"], Mine(await Goals.ListOpenAsync(Now, Ct)));
        Assert.Equal([$"goal_b{_s}"], Mine(await Goals.ListOpenAsync(Now.AddHours(10), Ct)));
        Assert.Equal([$"goal_a{_s}"], Mine(await Goals.ListDepartingAsync(Owner, Now, Now.AddHours(10), Ct)));
    }

    [Fact]
    public async Task Confirming_a_draft_stores_it_confirmed_with_its_commands_and_announcements()
    {
        var goal = NewGoal("cf", "car", "box", Now.AddHours(13));
        var draft = Version(goal, 1);
        await Goals.CreateAsync(goal, draft, Now, Ct);
        var batch = CommandSamples.ForPlan(draft, goal.Goal.ChargerDeviceId, Now);

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ConfirmAsync(draft, null, null, batch, Now.AddMinutes(2), Ct));

        var plan = await Plans.GetCurrentAsync(goal.Goal.Id, Ct);
        Assert.Equal((PlanStatus.Confirmed, Now.AddMinutes(2)), (plan!.Plan.Status, plan.ConfirmedAt!.Value));
        var commands = await new CommandStore(_database).ListForPlanVersionAsync(goal.PlanId, 1, Ct);
        Assert.Equal(4, commands.Count);
        Assert.All(commands, c => Assert.Equal(CommandState.Pending, c.Command.State));
        Assert.Equal(4, await Outbox.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("commandId", commands.Select(c => c.Id.Value)), cancellationToken: Ct));
    }

    [Fact]
    public async Task Confirming_twice_or_below_a_newer_version_writes_nothing()
    {
        var goal = NewGoal("c2", "car", "box", Now.AddHours(13));
        var draft = Version(goal, 1);
        await Goals.CreateAsync(goal, draft, Now, Ct);
        await Goals.ConfirmAsync(draft, null, null, CommandBatch.None, Now, Ct);

        var again = CommandSamples.ForPlan(draft, goal.Goal.ChargerDeviceId, Now);
        Assert.Equal(GoalWriteOutcome.PlanChanged, await Goals.ConfirmAsync(draft, null, null, again, Now, Ct));

        var other = NewGoal("c3", "car2", "box2", Now.AddHours(13));
        await Goals.CreateAsync(other, Version(other, 1), Now, Ct);
        await Plans.AddVersionAsync(Version(other, 2, reason: PlanReason.PricesPublished), Ct);
        Assert.Equal(GoalWriteOutcome.PlanChanged, await Goals.ConfirmAsync(Version(other, 1), null, null, CommandBatch.None, Now, Ct));

        Assert.Empty(await new CommandStore(_database).ListForPlanVersionAsync(goal.PlanId, 1, Ct));
        Assert.Equal(PlanStatus.Draft, (await Plans.GetCurrentAsync(other.Goal.Id, Ct))!.Plan.Status);
    }

    [Fact]
    public async Task Change_draft_keeps_the_running_version_and_supersedes_an_earlier_draft()
    {
        var (goal, running) = await ConfirmedAsync("cd");
        await Goals.AddChangeDraftAsync(Version(goal, 2) with { ProposedTargetSocPercent = 85 }, Ct);

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.AddChangeDraftAsync(Version(goal, 3) with { ProposedTargetSocPercent = 90 }, Ct));

        var versions = await Plans.ListVersionsAsync(goal.Goal.Id, Ct);
        Assert.Equal([PlanStatus.Confirmed, PlanStatus.Superseded, PlanStatus.Draft], versions.Select(v => v.Plan.Status));
        Assert.Equal(running.Plan.Version, (await Plans.GetRunningAsync(goal.Goal.Id, Ct))!.Plan.Version);
        Assert.Equal(80, (await Goals.GetAsync(Owner, goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
    }

    [Fact]
    public async Task Confirming_a_change_draft_applies_its_values_and_supersedes_the_running_version()
    {
        var (goal, running) = await ConfirmedAsync("ca");
        var later = goal.Goal.DepartureUtc.AddHours(2);
        var draft = Version(goal, 2) with { ProposedTargetSocPercent = 90, ProposedDepartureAt = later };
        await Goals.AddChangeDraftAsync(draft, Ct);
        var changed = goal with { Goal = new Goal(goal.Goal.Id, Owner, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, 90, later), UpdatedAt = Now.AddMinutes(5) };

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ConfirmAsync(draft, running, changed, CommandBatch.None, Now.AddMinutes(5), Ct));

        var stored = await Goals.GetAsync(Owner, goal.Goal.Id, Ct);
        Assert.Equal((90, later), (stored!.Goal.TargetSocPercent, stored.Goal.DepartureUtc));
        var claim = await Claims.Find(new BsonDocument("_id", goal.Goal.ChargerDeviceId.Value)).SingleAsync(Ct);
        Assert.Equal(later.UtcDateTime, claim["until"].ToUniversalTime());
        Assert.Equal([PlanStatus.Superseded, PlanStatus.Confirmed], (await Plans.ListVersionsAsync(goal.Goal.Id, Ct)).Select(v => v.Plan.Status));
    }

    [Fact]
    public async Task Re_planning_a_confirmed_plan_supersedes_the_running_version_and_a_change_draft()
    {
        var (goal, running) = await ConfirmedAsync("rp");
        await Goals.AddChangeDraftAsync(Version(goal, 2) with { ProposedTargetSocPercent = 90 }, Ct);
        var next = Version(goal, 3, PlanStatus.Confirmed, confirmedAt: running.ConfirmedAt, reason: PlanReason.PricesPublished);

        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ReplanConfirmedAsync(next, running, CommandBatch.None, Now.AddMinutes(5), Ct));

        Assert.Equal([PlanStatus.Superseded, PlanStatus.Superseded, PlanStatus.Confirmed], (await Plans.ListVersionsAsync(goal.Goal.Id, Ct)).Select(v => v.Plan.Status));
        Assert.Equal(3, (await Plans.GetRunningAsync(goal.Goal.Id, Ct))!.Plan.Version);
    }

    [Fact]
    public async Task Re_planning_a_version_that_is_no_longer_running_writes_nothing()
    {
        var (goal, running) = await ConfirmedAsync("rs");
        await Goals.ReplanConfirmedAsync(Version(goal, 2, PlanStatus.Confirmed, reason: PlanReason.PricesPublished), running, CommandBatch.None, Now, Ct);

        var stale = Version(goal, 3, PlanStatus.Confirmed, reason: PlanReason.ChargeDrift);
        Assert.Equal(GoalWriteOutcome.PlanChanged, await Goals.ReplanConfirmedAsync(stale, running, CommandBatch.None, Now, Ct));
        Assert.Equal(GoalWriteOutcome.VersionTaken, await Goals.ReplanConfirmedAsync(Version(goal, 2, PlanStatus.Confirmed), running, CommandBatch.None, Now, Ct));

        Assert.Equal(2, (await Plans.GetCurrentAsync(goal.Goal.Id, Ct))!.Plan.Version);
    }

    private IMongoCollection<BsonDocument> Outbox => _database.GetCollection<BsonDocument>(MongoIndexes.Outbox);

    private async Task<(StoredGoal Goal, StoredPlan Running)> ConfirmedAsync(string name)
    {
        var goal = NewGoal(name, "car", "box", Now.AddHours(13));
        var draft = Version(goal, 1);
        await Goals.CreateAsync(goal, draft, Now, Ct);
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.ConfirmAsync(draft, null, null, CommandBatch.None, Now, Ct));
        return (goal, (await Plans.GetRunningAsync(goal.Goal.Id, Ct))!);
    }

    // Only this test's goals: the database is shared with the other tests of the class.
    private IEnumerable<string> Mine(IEnumerable<StoredGoal> goals) =>
        goals.Select(g => g.Goal.Id.Value).Where(id => id.EndsWith(_s, StringComparison.Ordinal));

    private StoredGoal NewGoal(string name, string vehicle, string charger, DateTimeOffset departure, OwnerId? owner = null) =>
        Goal($"goal_{name}{_s}", $"dev_{vehicle}{_s}", $"dev_{charger}{_s}", departure, owner);
}
