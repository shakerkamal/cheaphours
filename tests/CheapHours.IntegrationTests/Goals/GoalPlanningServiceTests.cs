using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Core.Prices;
using CheapHours.Host.Goals;
using CheapHours.Host.Prices;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using CheapHours.Persistence.Prices;
using CheapHours.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Goals;

/// <summary>The shared goal planning path (charging-goals spec, design §5) against a real database.</summary>
public sealed class GoalPlanningServiceTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly DateTimeOffset Evening = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 11, 3, 23, 0, 0, TimeSpan.Zero); // 00:00 Berlin
    private static readonly DateTimeOffset Departure = new(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Evening);
    private readonly CapturingLoggerProvider _logs = new();
    private readonly string _zone = "Z" + SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;
    private GoalPlanningService _service = null!;
    private DeviceId _vehicle;
    private DeviceId _charger;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlanStore Plans => new(_database);

    public async ValueTask InitializeAsync()
    {
        _database = await SharedDatabases.GetAsync(mongo, nameof(GoalPlanningServiceTests));
        var deviceClock = new FakeTimeProvider(Evening.AddDays(-1));
        _vehicle = await SeedDeviceAsync(deviceClock, "carcloud", "veh-" + _zone, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 60_000));
        _charger = await SeedDeviceAsync(deviceClock, "ocpp", "cp-" + _zone, DeviceType.Charger, new DeviceAttributes(maxPowerW: 11_000));
        await SetChargeAsync(_vehicle, 40.0);

        // This evening (until midnight Berlin) is priced at 300; tomorrow is not published yet.
        await SeedPricesAsync(Evening, Midnight, 300m);

        using var loggers = LoggerFactory.Create(b => b.AddProvider(_logs));
        _service = new GoalPlanningService(
            new GoalStore(_database),
            new PlanStore(_database),
            new PriceStore(_database),
            new DeviceReader(_database),
            new CommandStore(_database),
            Options.Create(new PlannerOptions()),
            Options.Create(new PricesOptions { Zone = _zone }),
            _time,
            loggers.CreateLogger<GoalPlanningService>());
    }

    public ValueTask DisposeAsync()
    {
        _logs.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Creating_gives_draft_version_one_from_the_start_charge()
    {
        var planned = Assert.IsType<GoalOutcome.Planned>(await CreateAsync());

        var plan = planned.Plan;
        Assert.Equal(1, plan.Plan.Version);
        Assert.Equal(PlanStatus.Draft, plan.Plan.Status);
        Assert.Equal(PlanReason.Created, plan.Reason);
        Assert.Equal(40m, plan.StartSocPercent);
        Assert.Equal(26_667, plan.EnergyNeededWh);
        Assert.Equal(Evening.AddMinutes(10), plan.Plan.ExpiresAtUtc);

        // Before tomorrow's prices exist, the plan charges in tonight's priced slots (the safe fallback).
        Assert.Equal(10, plan.Plan.Slots.Count);
        Assert.Equal(Evening, plan.Plan.Slots[0].StartUtc);
        Assert.True(plan.Plan.PricesComplete);

        var stored = await Plans.GetCurrentAsync(planned.Goal.Goal.Id, Ct);
        Assert.Equal(plan.VersionId, stored!.VersionId);
    }

    [Fact]
    public async Task Second_goal_for_the_same_vehicle_is_a_conflict()
    {
        await CreateAsync();

        var second = await _service.CreateAsync(Owner, new GoalRequest(_vehicle, _charger, 90, Departure.AddHours(2)), Ct);

        Assert.Equal(new GoalOutcome.Conflict(GoalPlanningService.DeviceBusyDetail), second);
    }

    [Fact]
    public async Task Unchanged_re_plan_adds_no_version()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();

        var outcome = await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, reportedSocPercent: null, Ct);

        Assert.IsType<ReplanOutcome.Unchanged>(outcome);
        Assert.Single(await Plans.ListVersionsAsync(planned.Goal.Goal.Id, Ct));
    }

    [Fact]
    public async Task New_prices_re_plan_a_live_draft_with_a_fresh_expiry()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        _time.Advance(TimeSpan.FromMinutes(3));
        await SeedPricesAsync(Midnight, Departure, 80m);

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));

        Assert.Equal(2, added.Version.Plan.Version);
        Assert.Equal(PlanStatus.Draft, added.Version.Plan.Status);
        Assert.Equal(_time.GetUtcNow().AddMinutes(10), added.Version.Plan.ExpiresAtUtc);
        Assert.All(added.Version.Plan.Slots, s => Assert.True(s.StartUtc >= Midnight));
        Assert.Equal(PlanReason.PricesPublished, added.Version.Reason);
        Assert.Equal(PlanStatus.Superseded, (await Plans.ListVersionsAsync(planned.Goal.Goal.Id, Ct))[0].Plan.Status);
    }

    [Fact]
    public async Task Expired_draft_is_made_again_with_a_fresh_expiry()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.IsType<ReplanOutcome.Skipped>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));
        var again = Assert.IsType<GoalOutcome.Planned>(await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, null, null, Ct));

        Assert.Equal(2, again.Plan.Plan.Version);
        Assert.Equal(PlanStatus.Draft, again.Plan.Plan.Status);
        Assert.Equal(_time.GetUtcNow().AddMinutes(10), again.Plan.Plan.ExpiresAtUtc);
        Assert.Equal(PlanReason.GoalChanged, again.Plan.Reason);
    }

    [Fact]
    public async Task Confirmed_plan_stays_confirmed_with_its_confirmation_time()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        var confirmedAt = Evening.AddMinutes(2);
        await ConfirmAsync(planned.Plan, confirmedAt);
        _time.Advance(TimeSpan.FromHours(1));
        await SeedPricesAsync(Midnight, Departure, 80m);

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));

        Assert.Equal(PlanStatus.Confirmed, added.Version.Plan.Status);
        Assert.Equal(confirmedAt, added.Version.ConfirmedAt);
        Assert.Null(added.Version.Plan.ExpiresAtUtc);
        Assert.Equal(confirmedAt, (await Plans.GetCurrentAsync(planned.Goal.Goal.Id, Ct))!.ConfirmedAt);
    }

    // charging-goals: "Confirmed plan" (changing) and "Change draft above a confirmed version".
    [Fact]
    public async Task Changing_a_confirmed_goal_adds_a_change_draft_and_leaves_the_goal_and_the_running_version()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        await ConfirmViaServiceAsync(planned);

        var changed = Assert.IsType<GoalOutcome.Planned>(await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, 90, null, Ct));

        Assert.Equal(80, changed.Goal.Goal.TargetSocPercent);
        Assert.Equal((2, PlanStatus.Draft, PlanReason.GoalChanged), (changed.Plan.Plan.Version, changed.Plan.Plan.Status, changed.Plan.Reason));
        Assert.Equal((90, (DateTimeOffset?)Departure), (changed.Plan.ProposedTargetSocPercent!.Value, changed.Plan.ProposedDepartureAt));
        Assert.Equal(1, (await Plans.GetRunningAsync(planned.Goal.Goal.Id, Ct))!.Plan.Version);
        Assert.Equal(80, (await new GoalStore(_database).GetAsync(Owner, planned.Goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
        Assert.All(await new CommandStore(_database).ListForPlanVersionAsync(planned.Plan.Plan.Id, 1, Ct), c => Assert.Equal(CommandState.Pending, c.Command.State));
    }

    // charging-goals: "Change draft expires".
    [Fact]
    public async Task Expired_change_draft_changes_nothing_and_the_confirmed_version_keeps_running()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        await ConfirmViaServiceAsync(planned);
        await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, 90, null, Ct);

        _time.Advance(TimeSpan.FromMinutes(11));

        var current = await Plans.GetCurrentAsync(planned.Goal.Goal.Id, Ct);
        Assert.Equal(PlanStatus.Expired, current!.Plan.StatusAsOf(_time.GetUtcNow()));
        Assert.Equal((1, PlanStatus.Confirmed), ((await Plans.GetRunningAsync(planned.Goal.Goal.Id, Ct))!.Plan.Version, (await Plans.GetRunningAsync(planned.Goal.Goal.Id, Ct))!.Plan.Status));
        Assert.Equal(80, (await new GoalStore(_database).GetAsync(Owner, planned.Goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
        Assert.IsType<ConfirmOutcome.Expired>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));
    }

    // charging-goals: "Pending change draft".
    [Fact]
    public async Task New_prices_re_plan_the_running_version_from_the_stored_values_and_supersede_the_change_draft()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        await ConfirmViaServiceAsync(planned);
        await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, 90, null, Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        await SeedPricesAsync(Midnight, Departure, 80m);

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));

        Assert.Equal((3, PlanStatus.Confirmed, 80), (added.Version.Plan.Version, added.Version.Plan.Status, added.Version.TargetSocPercent));
        Assert.Equal([PlanStatus.Superseded, PlanStatus.Superseded, PlanStatus.Confirmed], (await Plans.ListVersionsAsync(planned.Goal.Goal.Id, Ct)).Select(v => v.Plan.Status));
        var commands = new CommandStore(_database);
        Assert.All(await commands.ListForPlanVersionAsync(planned.Plan.Plan.Id, 1, Ct), c => Assert.Equal(CommandState.Cancelled, c.Command.State));
        Assert.NotEmpty(await commands.ListForPlanVersionAsync(planned.Plan.Plan.Id, 3, Ct));
    }

    // charging-goals: "Charging continues".
    [Fact]
    public async Task Re_plan_while_charging_continues_the_run_without_a_new_start()
    {
        await SetChargeAsync(_vehicle, 75.0);
        var planned = (GoalOutcome.Planned)await CreateAsync();
        Assert.Equal([Evening, Evening.AddMinutes(15)], planned.Plan.Plan.Slots.Select(s => s.StartUtc));
        var v1 = await ConfirmViaServiceAsync(planned);
        _time.Advance(TimeSpan.FromMinutes(5));
        await MarkSentAsync(v1.Single(c => c.Command.Request.Type == CommandType.StartCharging));

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));

        Assert.Equal([Evening.AddMinutes(15), Evening.AddMinutes(30)], added.Version.Plan.Slots.Select(s => s.StartUtc));
        var v2 = await new CommandStore(_database).ListForPlanVersionAsync(planned.Plan.Plan.Id, 2, Ct);
        var stop = Assert.Single(v2);
        Assert.Equal((CommandType.StopCharging, Evening.AddMinutes(45)), (stop.Command.Request.Type, stop.Command.DueAt));
        var old = await new CommandStore(_database).ListForPlanVersionAsync(planned.Plan.Plan.Id, 1, Ct);
        Assert.Equal(CommandState.Sent, old.Single(c => c.Command.Request.Type == CommandType.StartCharging).Command.State);
        Assert.Equal(CommandState.Cancelled, old.Single(c => c.Command.Request.Type == CommandType.StopCharging).Command.State);
    }

    // charging-goals: "Charging moves later".
    [Fact]
    public async Task Re_plan_while_charging_into_later_slots_stops_at_the_end_of_the_current_slot()
    {
        await SetChargeAsync(_vehicle, 75.0);
        var planned = (GoalOutcome.Planned)await CreateAsync();
        var v1 = await ConfirmViaServiceAsync(planned);
        _time.Advance(TimeSpan.FromMinutes(5));
        await MarkSentAsync(v1.Single(c => c.Command.Request.Type == CommandType.StartCharging));

        // Tonight's prices are now dear except 22:00–22:30.
        await _database.GetCollection<BsonDocument>(MongoIndexes.Prices).DeleteManyAsync(new BsonDocument("zone", _zone), Ct);
        await SeedPricesAsync(Evening, Midnight, 900m);
        await _database.GetCollection<BsonDocument>(MongoIndexes.Prices).DeleteManyAsync(
            new BsonDocument { { "zone", _zone }, { "start", new BsonDocument("$gte", Evening.AddHours(4).UtcDateTime) } },
            Ct);
        await SeedPricesAsync(Evening.AddHours(4), Evening.AddHours(4).AddMinutes(30), 10m);

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct));

        Assert.Equal([Evening.AddHours(4), Evening.AddHours(4).AddMinutes(15)], added.Version.Plan.Slots.Select(s => s.StartUtc));
        var v2 = await new CommandStore(_database).ListForPlanVersionAsync(planned.Plan.Plan.Id, 2, Ct);
        Assert.Equal(
            [(CommandType.StopCharging, Evening.AddMinutes(15)), (CommandType.StartCharging, Evening.AddHours(4)), (CommandType.StopCharging, Evening.AddHours(4).AddMinutes(30))],
            v2.Select(c => (c.Command.Request.Type, c.Command.DueAt)));
    }

    private async Task<IReadOnlyList<StoredCommand>> ConfirmViaServiceAsync(GoalOutcome.Planned planned)
    {
        var confirmed = Assert.IsType<ConfirmOutcome.Confirmed>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));
        return confirmed.Commands;
    }

    // The start was handed to the charger: Published, then Accepted.
    private async Task MarkSentAsync(StoredCommand command)
    {
        var store = new CommandStore(_database);
        var loaded = (await store.FindAsync(command.Id, Ct))!;
        loaded.Command.Publish(_time.GetUtcNow());
        loaded.Command.Accept(_time.GetUtcNow(), "ocpp-1");
        Assert.NotNull(await store.SaveAsync(loaded, Ct));
    }

    [Fact]
    public async Task Changing_the_target_gives_a_new_draft_version()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();

        var changed = Assert.IsType<GoalOutcome.Planned>(await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, 90, null, Ct));

        Assert.Equal(90, changed.Goal.Goal.TargetSocPercent);
        Assert.Equal(2, changed.Plan.Plan.Version);
        Assert.Equal(PlanReason.GoalChanged, changed.Plan.Reason);
        Assert.Equal(90, changed.Plan.TargetSocPercent);
    }

    [Fact]
    public async Task Missing_charge_is_skipped_with_a_warning()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        await _database.GetCollection<BsonDocument>(MongoIndexes.Devices).UpdateOneAsync(
            new BsonDocument("_id", _vehicle.Value), new BsonDocument("$unset", new BsonDocument("lastReading", "")), cancellationToken: Ct);

        var outcome = await _service.ReplanAsync(planned.Goal, PlanReason.PricesPublished, null, Ct);

        Assert.IsType<ReplanOutcome.Skipped>(outcome);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("not reported a charge", StringComparison.Ordinal));
        Assert.Single(await Plans.ListVersionsAsync(planned.Goal.Goal.Id, Ct));
    }

    [Fact]
    public async Task Drift_re_plans_from_the_reported_charge()
    {
        var planned = (GoalOutcome.Planned)await CreateAsync();
        await ConfirmAsync(planned.Plan, Evening.AddMinutes(1));

        var added = Assert.IsType<ReplanOutcome.Added>(await _service.ReplanAsync(planned.Goal, PlanReason.ChargeDrift, reportedSocPercent: 30m, Ct));

        Assert.Equal(30m, added.Version.StartSocPercent);
        Assert.Equal(PlanReason.ChargeDrift, added.Version.Reason);
        Assert.Equal(PlanStatus.Confirmed, added.Version.Plan.Status);
    }

    private Task<GoalOutcome> CreateAsync() =>
        _service.CreateAsync(Owner, new GoalRequest(_vehicle, _charger, 80, Departure), Ct);

    private async Task ConfirmAsync(StoredPlan plan, DateTimeOffset at) =>
        await _database.GetCollection<BsonDocument>(MongoIndexes.Plans).UpdateOneAsync(
            new BsonDocument("_id", plan.VersionId),
            new BsonDocument
            {
                { "$set", new BsonDocument { { "status", "Confirmed" }, { "confirmedAt", at.UtcDateTime } } },
                { "$unset", new BsonDocument("expiresAt", "") },
            },
            cancellationToken: Ct);

    private async Task<DeviceId> SeedDeviceAsync(FakeTimeProvider clock, string provider, string externalId, DeviceType type, DeviceAttributes attributes)
    {
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var device = new Device(DeviceId.New(clock), Owner, provider, externalId, type, Capabilities.ReadTelemetry, attributes);
        return await new DeviceStateStore(_database).EnsureAsync(device, TimeSpan.FromMinutes(1), clock.GetUtcNow(), Ct);
    }

    private Task<UpdateResult> SetChargeAsync(DeviceId device, double socPercent) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.Devices).UpdateOneAsync(
            new BsonDocument("_id", device.Value),
            new BsonDocument("$set", new BsonDocument("lastReading", new BsonDocument
            {
                { "ts", Evening.AddMinutes(-5).UtcDateTime },
                { "energyWh", 0L },
                { "powerW", 0 },
                { "socPercent", socPercent },
            })),
            cancellationToken: Ct);

    private Task<PriceWriteResult> SeedPricesAsync(DateTimeOffset from, DateTimeOffset to, decimal price)
    {
        var slots = new List<PriceSlot>();
        for (var s = from; s < to; s = s.AddMinutes(15))
        {
            slots.Add(new PriceSlot(_zone, s, price));
        }

        return new PriceStore(_database).UpsertAsync(slots, Evening, Ct);
    }
}
