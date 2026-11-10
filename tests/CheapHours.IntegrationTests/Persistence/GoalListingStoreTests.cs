using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using static CheapHours.IntegrationTests.Persistence.GoalPlanSamples;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>
/// The owner's goal listing and the batch read of current plan versions behind <c>GET /v1/goals</c> (design §8).
/// Each test lists its own owner, so tests sharing the database never see each other's goals.
/// </summary>
public sealed class GoalListingStoreTests(MongoFixture mongo) : IAsyncLifetime
{
    private readonly string _s = SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;

    private GoalStore Goals => new(_database);

    private PlanStore Plans => new(_database);

    private OwnerId Mine => OwnerId.Parse($"own_list{_s}");

    private OwnerId Other => OwnerId.Parse($"own_other{_s}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await SharedDatabases.GetAsync(mongo, nameof(GoalListingStoreTests));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Goals_are_listed_newest_first()
    {
        var first = await CreateAsync("1", Mine);
        var second = await CreateAsync("2", Mine);
        var third = await CreateAsync("3", Mine);

        var listed = await Goals.ListAsync(Mine, before: null, take: 10, Ct);

        Assert.Equal([third.Goal.Id, second.Goal.Id, first.Goal.Id], listed.Select(g => g.Goal.Id));
        Assert.Equal(third, listed[0]);
    }

    [Fact]
    public async Task Before_continues_below_a_goal_and_take_limits_the_page()
    {
        var first = await CreateAsync("1", Mine);
        var second = await CreateAsync("2", Mine);
        var third = await CreateAsync("3", Mine);

        Assert.Equal([third.Goal.Id, second.Goal.Id], (await Goals.ListAsync(Mine, null, 2, Ct)).Select(g => g.Goal.Id));
        Assert.Equal([first.Goal.Id], (await Goals.ListAsync(Mine, second.Goal.Id, 2, Ct)).Select(g => g.Goal.Id));
        Assert.Empty(await Goals.ListAsync(Mine, first.Goal.Id, 2, Ct));
    }

    [Fact]
    public async Task Another_owners_goals_are_never_listed()
    {
        var mine = await CreateAsync("1", Mine);
        await CreateAsync("2", Other);

        var listed = await Goals.ListAsync(Mine, null, 10, Ct);

        Assert.Equal([mine.Goal.Id], listed.Select(g => g.Goal.Id));
    }

    [Fact]
    public async Task Current_version_is_read_for_each_goal_in_one_call()
    {
        var plain = await CreateAsync("1", Mine);
        var replanned = await CreateAsync("2", Mine);
        Assert.True(await Plans.AddVersionAsync(Version(replanned, 2, PlanStatus.Confirmed, confirmedAt: Now), Ct));
        var changed = await CreateAsync("3", Mine);
        await Plans.AddVersionAsync(Version(changed, 2, PlanStatus.Confirmed, confirmedAt: Now), Ct);
        var changeDraft = Version(changed, 3) with { ProposedTargetSocPercent = 90 };
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.AddChangeDraftAsync(changeDraft, Ct));

        var current = await Plans.GetCurrentManyAsync(Mine, [plain.Goal.Id, replanned.Goal.Id, changed.Goal.Id], Ct);

        var byGoal = current.ToDictionary(p => p.Plan.GoalId);
        Assert.Equal(3, byGoal.Count);
        Assert.Equal((1, PlanStatus.Draft), (byGoal[plain.Goal.Id].Plan.Version, byGoal[plain.Goal.Id].Plan.Status));
        Assert.Equal((2, PlanStatus.Confirmed), (byGoal[replanned.Goal.Id].Plan.Version, byGoal[replanned.Goal.Id].Plan.Status));
        Assert.Equal((3, PlanStatus.Draft), (byGoal[changed.Goal.Id].Plan.Version, byGoal[changed.Goal.Id].Plan.Status));
        Assert.True(byGoal[changed.Goal.Id].IsChangeDraft);
    }

    [Fact]
    public async Task Another_owners_goal_ids_read_nothing()
    {
        var theirs = await CreateAsync("1", Other);

        Assert.Empty(await Plans.GetCurrentManyAsync(Mine, [theirs.Goal.Id], Ct));
        Assert.Empty(await Plans.GetCurrentManyAsync(Mine, [], Ct));
    }

    // Ids sort in the order the goals are made, as UUIDv7 ids do.
    private async Task<StoredGoal> CreateAsync(string order, OwnerId owner)
    {
        var name = $"{order}{owner.Value["own_".Length..]}";
        var goal = Goal($"goal_{name}", $"dev_car{name}", $"dev_box{name}", Now.AddHours(13), owner);
        Assert.Equal(GoalWriteOutcome.Stored, await Goals.CreateAsync(goal, Version(goal, 1), Now, Ct));
        return goal;
    }
}
