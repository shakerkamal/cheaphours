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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Goals;

/// <summary>
/// Confirming a plan through the shared planning path (charging-goals spec: confirming a plan turns its runs into
/// commands; a plan's commands go to the goal's charger; planning-api: plans that cannot be confirmed are refused).
/// </summary>
public sealed class GoalConfirmationTests(MongoFixture mongo) : IAsyncLifetime
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly DateTimeOffset Evening = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Departure = new(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Evening);
    private readonly string _zone = "Z" + SharedDatabases.NewSuffix();
    private IMongoDatabase _database = null!;
    private GoalPlanningService _service = null!;
    private DeviceId _vehicle;
    private DeviceId _charger;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommandStore Commands => new(_database);

    public async ValueTask InitializeAsync()
    {
        _database = await SharedDatabases.GetAsync(mongo, nameof(GoalConfirmationTests));
        var clock = new FakeTimeProvider(Evening.AddDays(-1));
        _vehicle = await DeviceAsync(clock, "carcloud", "veh-" + _zone, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 60_000));
        _charger = await DeviceAsync(clock, "ocpp", "cp-" + _zone, DeviceType.Charger, new DeviceAttributes(maxPowerW: 11_000));

        // 70 % of a 60 kWh battery to 80 % needs three slots at 11 kW. Three cheap slots form two runs.
        await ChargeAsync(70.0);
        await PricesAsync(Evening, Evening.AddHours(5), 300m);
        await _database.GetCollection<BsonDocument>(MongoIndexes.Prices).DeleteManyAsync(
            new BsonDocument { { "zone", _zone }, { "start", new BsonDocument("$in", new BsonArray { Evening.AddMinutes(15).UtcDateTime, Evening.AddMinutes(30).UtcDateTime, Evening.AddHours(1).UtcDateTime }) } },
            Ct);
        foreach (var cheap in new[] { Evening.AddMinutes(15), Evening.AddMinutes(30), Evening.AddHours(1) })
        {
            await PricesAsync(cheap, cheap.AddMinutes(15), 10m);
        }

        _service = new GoalPlanningService(
            new GoalStore(_database),
            new PlanStore(_database),
            new PriceStore(_database),
            new DeviceReader(_database),
            new CommandStore(_database),
            Options.Create(new PlannerOptions()),
            Options.Create(new PricesOptions { Zone = _zone }),
            _time,
            NullLogger<GoalPlanningService>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // charging-goals: "Two runs"; planning-api: "Draft with two runs".
    [Fact]
    public async Task Two_runs_give_a_start_and_a_stop_each_keyed_by_the_version_and_slot()
    {
        var planned = await CreateAsync();
        Assert.Equal([Evening.AddMinutes(15), Evening.AddMinutes(30), Evening.AddHours(1)], planned.Plan.Plan.Slots.Select(s => s.StartUtc));
        _time.Advance(TimeSpan.FromMinutes(2));

        var confirmed = Assert.IsType<ConfirmOutcome.Confirmed>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        var plan = planned.Plan.Plan.Id.Value;
        Assert.Equal(
            [
                (CommandType.StartCharging, Evening.AddMinutes(15), $"{plan}:v1:slot_2026-11-03T18:15Z:StartCharging"),
                (CommandType.StopCharging, Evening.AddMinutes(45), $"{plan}:v1:slot_2026-11-03T18:30Z:StopCharging"),
                (CommandType.StartCharging, Evening.AddHours(1), $"{plan}:v1:slot_2026-11-03T19:00Z:StartCharging"),
                (CommandType.StopCharging, Evening.AddMinutes(75), $"{plan}:v1:slot_2026-11-03T19:00Z:StopCharging"),
            ],
            confirmed.Commands.Select(c => (c.Command.Request.Type, c.Command.DueAt, c.Command.IdempotencyKey)));
        Assert.Equal((PlanStatus.Confirmed, Evening.AddMinutes(2)), (confirmed.Plan.Plan.Status, confirmed.Plan.ConfirmedAt!.Value));
        var stored = await Commands.ListForPlanVersionAsync(planned.Plan.Plan.Id, 1, Ct);
        Assert.Equal(4, stored.Count);
        Assert.All(stored, c => Assert.Equal(CommandState.Pending, c.Command.State));
    }

    // charging-goals: "Wallbox and car".
    [Fact]
    public async Task Every_command_targets_the_goals_charger()
    {
        var planned = await CreateAsync();

        var confirmed = Assert.IsType<ConfirmOutcome.Confirmed>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        Assert.All(confirmed.Commands, c => Assert.Equal(_charger, c.Command.Request.DeviceId));
    }

    // charging-goals: "Nothing to charge".
    [Fact]
    public async Task Draft_with_no_slots_is_confirmed_without_commands()
    {
        var planned = await CreateAsync(target: 70);
        Assert.Empty(planned.Plan.Plan.Slots);

        var confirmed = Assert.IsType<ConfirmOutcome.Confirmed>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        Assert.Equal(PlanStatus.Confirmed, confirmed.Plan.Plan.Status);
        Assert.Empty(confirmed.Commands);
    }

    // charging-goals: "Change draft confirmed".
    [Fact]
    public async Task Confirming_a_change_draft_applies_it_and_cancels_the_old_versions_unsent_commands()
    {
        var planned = await CreateAsync();
        await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct);
        var draft = Assert.IsType<GoalOutcome.Planned>(await _service.ChangeAsync(Owner, planned.Goal.Goal.Id, 90, null, Ct));
        _time.Advance(TimeSpan.FromMinutes(1));

        var confirmed = Assert.IsType<ConfirmOutcome.Confirmed>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        Assert.Equal(2, confirmed.Plan.Plan.Version);
        Assert.Equal(90, confirmed.Goal.Goal.TargetSocPercent);
        Assert.Equal(90, (await new GoalStore(_database).GetAsync(Owner, planned.Goal.Goal.Id, Ct))!.Goal.TargetSocPercent);
        var old = await Commands.ListForPlanVersionAsync(planned.Plan.Plan.Id, 1, Ct);
        Assert.All(old, c => Assert.Equal(CommandState.Cancelled, c.Command.State));
        Assert.All(old, c => Assert.Equal($"Superseded by {draft.Plan.VersionId}", c.Command.History[^1].Reason));
        Assert.Equal(PlanStatus.Superseded, (await new PlanStore(_database).ListVersionsAsync(planned.Goal.Goal.Id, Ct))[0].Plan.Status);
    }

    // planning-api: "Confirmed twice with different keys".
    [Fact]
    public async Task Confirmed_plan_cannot_be_confirmed_again()
    {
        var planned = await CreateAsync();
        await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct);

        Assert.IsType<ConfirmOutcome.Conflict>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        Assert.Equal(4, (await Commands.ListForPlanAsync(planned.Plan.Plan.Id, Ct)).Count);
    }

    // planning-api: "Draft expired".
    [Fact]
    public async Task Draft_confirmed_after_its_expiry_is_gone()
    {
        var planned = await CreateAsync();
        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.IsType<ConfirmOutcome.Expired>(await _service.ConfirmAsync(Owner, planned.Plan.Plan.Id, Ct));

        Assert.Empty(await Commands.ListForPlanAsync(planned.Plan.Plan.Id, Ct));
    }

    [Fact]
    public async Task Plan_of_another_owner_or_none_is_not_found()
    {
        var planned = await CreateAsync();

        Assert.IsType<ConfirmOutcome.NotFound>(await _service.ConfirmAsync(OwnerId.Parse("own_other"), planned.Plan.Plan.Id, Ct));
        Assert.IsType<ConfirmOutcome.NotFound>(await _service.ConfirmAsync(Owner, PlanId.Parse("plan_missing"), Ct));
        Assert.Empty(await Commands.ListForPlanAsync(planned.Plan.Plan.Id, Ct));
    }

    private async Task<GoalOutcome.Planned> CreateAsync(int target = 80) =>
        Assert.IsType<GoalOutcome.Planned>(await _service.CreateAsync(Owner, new GoalRequest(_vehicle, _charger, target, Departure), Ct));

    private async Task<DeviceId> DeviceAsync(FakeTimeProvider clock, string provider, string externalId, DeviceType type, DeviceAttributes attributes)
    {
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var device = new Device(DeviceId.New(clock), Owner, provider, externalId, type, Capabilities.ReadTelemetry, attributes);
        return await new DeviceStateStore(_database).EnsureAsync(device, TimeSpan.FromMinutes(1), clock.GetUtcNow(), Ct);
    }

    private Task<UpdateResult> ChargeAsync(double socPercent) =>
        _database.GetCollection<BsonDocument>(MongoIndexes.Devices).UpdateOneAsync(
            new BsonDocument("_id", _vehicle.Value),
            new BsonDocument("$set", new BsonDocument("lastReading", new BsonDocument
            {
                { "ts", Evening.AddMinutes(-5).UtcDateTime },
                { "energyWh", 0L },
                { "powerW", 0 },
                { "socPercent", socPercent },
            })),
            cancellationToken: Ct);

    private Task<PriceWriteResult> PricesAsync(DateTimeOffset from, DateTimeOffset to, decimal price)
    {
        var slots = new List<PriceSlot>();
        for (var s = from; s < to; s = s.AddMinutes(15))
        {
            slots.Add(new PriceSlot(_zone, s, price));
        }

        return new PriceStore(_database).UpsertAsync(slots, Evening, Ct);
    }
}
