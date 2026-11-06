using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Host.Goals;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence.Plans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Goals;

/// <summary>The charge drift check (charging-goals spec, design §6) on real worker hosts.</summary>
public sealed class DriftCheckTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    // One database and broker prefix for the class: each test uses its own goals and devices.
    private static readonly string DatabaseName = MongoFixture.NewDatabaseName(nameof(DriftCheckTests));
    private static readonly string Prefix = RabbitMqFixture.NewPrefix(nameof(DriftCheckTests));
    private readonly List<IHost> _workers = [];
    private IMongoDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlanStore Plans => new(_database);

    public async ValueTask InitializeAsync()
    {
        await StartWorkerAsync("replica-a");
        _database = mongo.Client.GetDatabase(DatabaseName);
        var now = DateTimeOffset.UtcNow;
        await GoalSeeds.PricesAsync(_database, now, now.AddHours(14), 120m);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await WorkerHost.StopAsync(worker);
        }
    }

    [Fact]
    public async Task An_eight_point_drift_adds_a_confirmed_version_from_the_reported_charge()
    {
        var (goal, vehicle) = await ConfirmedGoalAsync("a");
        await GoalSeeds.SetChargeAsync(_database, vehicle, 32.0, DateTimeOffset.UtcNow);

        Assert.True(await Drift(_workers[0]).CheckOnceAsync(Ct));

        var versions = await Plans.ListVersionsAsync(goal, Ct);
        Assert.Equal(2, versions.Count);
        Assert.Equal(PlanReason.ChargeDrift, versions[1].Reason);
        Assert.Equal(PlanStatus.Confirmed, versions[1].Plan.Status);
        Assert.Equal(32m, versions[1].StartSocPercent);
        Assert.NotNull(versions[1].ConfirmedAt);

        // The same reading is not newer than the new version, so the next check adds nothing.
        await Drift(_workers[0]).CheckOnceAsync(Ct);
        Assert.Equal(2, (await Plans.ListVersionsAsync(goal, Ct)).Count);
    }

    [Fact]
    public async Task A_two_point_drift_adds_nothing()
    {
        var (goal, vehicle) = await ConfirmedGoalAsync("a");
        await GoalSeeds.SetChargeAsync(_database, vehicle, 38.0, DateTimeOffset.UtcNow);

        await Drift(_workers[0]).CheckOnceAsync(Ct);

        Assert.Single(await Plans.ListVersionsAsync(goal, Ct));
    }

    [Fact]
    public async Task Drafts_and_stale_readings_are_ignored()
    {
        var draft = await CreateGoalAsync("draft");
        await GoalSeeds.SetChargeAsync(_database, draft.Vehicle, 20.0, DateTimeOffset.UtcNow);
        var (stale, staleVehicle) = await ConfirmedGoalAsync("stale");
        await GoalSeeds.SetChargeAsync(_database, staleVehicle, 20.0, DateTimeOffset.UtcNow.AddHours(-1));

        await Drift(_workers[0]).CheckOnceAsync(Ct);

        Assert.Single(await Plans.ListVersionsAsync(draft.Goal, Ct));
        Assert.Single(await Plans.ListVersionsAsync(stale, Ct));
    }

    [Fact]
    public async Task Two_workers_run_one_check()
    {
        var second = await StartWorkerAsync("replica-b");
        var (goal, vehicle) = await ConfirmedGoalAsync("a");
        await GoalSeeds.SetChargeAsync(_database, vehicle, 30.0, DateTimeOffset.UtcNow);

        var ran = await Task.WhenAll(Drift(_workers[0]).CheckOnceAsync(Ct), Drift(second).CheckOnceAsync(Ct));

        Assert.Single(ran, r => r);
        Assert.Equal(2, (await Plans.ListVersionsAsync(goal, Ct)).Count);
    }

    private static DriftCheckService Drift(IHost worker) => worker.Services.GetRequiredService<DriftCheckService>();

    private async Task<IHost> StartWorkerAsync(string replica)
    {
        var worker = await WorkerHost.StartAsync(mongo, rabbit, DatabaseName, Prefix, new Dictionary<string, string> { ["Worker:ReplicaId"] = replica });
        _workers.Add(worker);
        return worker;
    }

    private async Task<(GoalId Goal, DeviceId Vehicle)> CreateGoalAsync(string suffix)
    {
        var (vehicle, charger) = await GoalSeeds.CarAndWallboxAsync(_database, Owner, suffix, readingAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var planning = _workers[0].Services.GetRequiredService<GoalPlanningService>();
        var planned = Assert.IsType<GoalOutcome.Planned>(await planning.CreateAsync(Owner, new GoalRequest(vehicle, charger, 80, DateTimeOffset.UtcNow.AddHours(13)), Ct));
        return (planned.Goal.Goal.Id, vehicle);
    }

    private async Task<(GoalId Goal, DeviceId Vehicle)> ConfirmedGoalAsync(string suffix)
    {
        var (goal, vehicle) = await CreateGoalAsync(suffix);
        var plan = await Plans.GetCurrentAsync(goal, Ct);
        await GoalSeeds.ConfirmAsync(_database, plan!.VersionId, DateTimeOffset.UtcNow);
        return (goal, vehicle);
    }
}
