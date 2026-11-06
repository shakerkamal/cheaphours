using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
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

    // Only this test's goals: the database is shared with the other tests of the class.
    private IEnumerable<string> Mine(IEnumerable<StoredGoal> goals) =>
        goals.Select(g => g.Goal.Id.Value).Where(id => id.EndsWith(_s, StringComparison.Ordinal));

    private StoredGoal NewGoal(string name, string vehicle, string charger, DateTimeOffset departure, OwnerId? owner = null) =>
        Goal($"goal_{name}{_s}", $"dev_{vehicle}{_s}", $"dev_{charger}{_s}", departure, owner);
}
