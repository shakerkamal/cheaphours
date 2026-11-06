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
/// Plan versions (charging-goals spec, design §3). The tests share one database and keep apart by a per-test
/// suffix on every goal and device id.
/// </summary>
public sealed class PlanStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private GoalStore Goals => new(_database);

    private PlanStore Plans => new(_database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(PlanStoreTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Re_planned_twice_gives_three_versions_under_one_plan_id()
    {
        var goal = await CreateAsync("a");

        Assert.True(await Plans.AddVersionAsync(Version(goal, 2, reason: PlanReason.PricesPublished), Ct));
        Assert.True(await Plans.AddVersionAsync(Version(goal, 3, reason: PlanReason.GoalChanged), Ct));

        var versions = await Plans.ListVersionsAsync(goal.Goal.Id, Ct);
        Assert.Equal([1, 2, 3], versions.Select(v => v.Plan.Version));
        Assert.All(versions, v => Assert.Equal(goal.PlanId, v.Plan.Id));
        Assert.Equal([PlanStatus.Superseded, PlanStatus.Superseded, PlanStatus.Draft], versions.Select(v => v.Plan.Status));
        Assert.Equal(3, (await Plans.GetCurrentAsync(goal.Goal.Id, Ct))!.Plan.Version);
    }

    [Fact]
    public async Task Two_concurrent_version_two_writes_store_exactly_one()
    {
        var goal = await CreateAsync("a");

        var stored = await Task.WhenAll(
            Plans.AddVersionAsync(Version(goal, 2, reason: PlanReason.PricesPublished, expected: 1.0m), Ct),
            Plans.AddVersionAsync(Version(goal, 2, reason: PlanReason.ChargeDrift, expected: 0.9m), Ct));

        Assert.Single(stored, s => s);
        var versions = await Plans.ListVersionsAsync(goal.Goal.Id, Ct);
        Assert.Equal([1, 2], versions.Select(v => v.Plan.Version));
        Assert.Equal(PlanStatus.Superseded, versions[0].Plan.Status);
    }

    [Fact]
    public async Task A_version_round_trips_with_what_it_was_made_from()
    {
        var goal = await CreateAsync("a");
        var confirmedAt = Now.AddMinutes(3);
        var next = Version(goal, 2, PlanStatus.Confirmed, madeAt: Now.AddHours(1), confirmedAt: confirmedAt, reason: PlanReason.ChargeDrift, expected: -0.0275m, arrival: 1.595m, pricesComplete: false);

        await Plans.AddVersionAsync(next, Ct);

        var read = await Plans.GetCurrentAsync(goal.Goal.Id, Ct);
        Assert.Equal(next.Plan.Slots, read!.Plan.Slots);
        Assert.Equal(-0.0275m, read.Plan.ExpectedCostEur);
        Assert.Equal(1.595m, read.Plan.ArrivalCostEur);
        Assert.False(read.Plan.PricesComplete);
        Assert.Equal(Now.AddHours(1), read.Plan.MadeAtUtc);
        Assert.Null(read.Plan.ExpiresAtUtc);
        Assert.Equal(confirmedAt, read.ConfirmedAt);
        Assert.Equal(PlanReason.ChargeDrift, read.Reason);
        Assert.Equal(40.5m, read.StartSocPercent);
        Assert.Equal(26_667, read.EnergyNeededWh);

        var raw = await _database.GetCollection<BsonDocument>(MongoIndexes.Plans).Find(new BsonDocument("_id", $"plan_a{_s}:v2")).SingleAsync(Ct);
        Assert.Equal("ChargeDrift", raw["reason"].AsString);
        Assert.Equal("Confirmed", raw["status"].AsString);
        Assert.Equal(new Decimal128(1.595m), raw["arrivalCostEur"].AsDecimal128);
    }

    [Fact]
    public async Task Latest_confirmed_version_per_goal_is_read_for_savings()
    {
        var a = await CreateAsync("a");
        await Plans.AddVersionAsync(Version(a, 2, PlanStatus.Confirmed, confirmedAt: Now, expected: 1.10m), Ct);
        await Plans.AddVersionAsync(Version(a, 3, PlanStatus.Confirmed, confirmedAt: Now, reason: PlanReason.PricesPublished, expected: 0.80m), Ct);
        var b = await CreateAsync("b", "car2", "box2");
        var draftsOnly = await CreateAsync("c", "car3", "box3");

        var latest = await Plans.LatestConfirmedAsync(Owner, [a.Goal.Id, b.Goal.Id, draftsOnly.Goal.Id], Ct);

        var only = Assert.Single(latest);
        Assert.Equal(3, only.Plan.Version);
        Assert.Equal(0.80m, only.Plan.ExpectedCostEur);
        Assert.Empty(await Plans.LatestConfirmedAsync(OwnerId.Parse("own_other"), [a.Goal.Id], Ct));
    }

    private async Task<StoredGoal> CreateAsync(string name, string vehicle = "car", string charger = "box")
    {
        var goal = Goal($"goal_{name}{_s}", $"dev_{vehicle}{_s}", $"dev_{charger}{_s}", Now.AddHours(13));
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct));
        return goal;
    }
}
