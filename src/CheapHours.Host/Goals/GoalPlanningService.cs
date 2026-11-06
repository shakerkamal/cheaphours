using CheapHours.Core.Devices;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Core.Prices;
using CheapHours.Host.Prices;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using CheapHours.Persistence.Prices;
using CheapHours.Planning;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Goals;

/// <summary>
/// The one planning path for the API and the worker (design §5). It reads the goal's vehicle and charger
/// owner-scoped and the window's prices, runs the planner, compares the result with the current version and
/// stores a new version only when something changed. <c>Created</c> and <c>GoalChanged</c> give a draft that
/// expires; <c>PricesPublished</c> and <c>ChargeDrift</c> keep the current status, so a confirmed plan stays
/// confirmed. A version another writer stored first is retried once from a fresh read, then dropped.
/// </summary>
internal sealed partial class GoalPlanningService(
    IGoalStore goals,
    IPlanStore plans,
    IPriceStore prices,
    IDeviceReader devices,
    IOptions<PlannerOptions> plannerOptions,
    IOptions<PricesOptions> pricesOptions,
    TimeProvider time,
    ILogger<GoalPlanningService> logger)
{
    /// <summary>The 409 detail for a vehicle or charger that already has an open goal.</summary>
    public const string DeviceBusyDetail =
        "The vehicle or the charger already has an open goal. Change that goal with PATCH /v1/goals/{id} instead.";

    private readonly PlannerOptions _planner = plannerOptions.Value;

    /// <summary>Checks, plans and stores a new goal for <paramref name="ownerId"/>.</summary>
    /// <param name="ownerId">The owner of the calling key (D3).</param>
    /// <param name="request">The goal.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The stored goal and its draft, or why not.</returns>
    public async Task<GoalOutcome> CreateAsync(OwnerId ownerId, GoalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        if (CheckDeparture(request.DepartureUtc, now) is { } departureProblem)
        {
            return departureProblem;
        }

        var resolved = await ResolveAsync(ownerId, request.VehicleId, request.ChargerId, request.TargetSocPercent, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem is { } problem)
        {
            return problem;
        }

        var inputs = resolved.Devices!;
        var goal = new StoredGoal(
            new Goal(GoalId.New(time), ownerId, request.VehicleId, request.ChargerId, request.TargetSocPercent, request.DepartureUtc),
            PlanId.New(time),
            now,
            now);
        var result = await PlanAsync(goal, inputs, inputs.SocPercent!.Value, now, cancellationToken).ConfigureAwait(false);
        var first = Version(goal, result, inputs.SocPercent.Value, PlanReason.Created, version: 1, current: null, now);

        return await goals.CreateAsync(goal, first, now, cancellationToken).ConfigureAwait(false) == GoalWriteOutcome.Stored
            ? new GoalOutcome.Planned(goal, first)
            : new GoalOutcome.Conflict(DeviceBusyDetail);
    }

    /// <summary>Changes an open goal's target or departure and re-plans it as a new draft.</summary>
    /// <param name="ownerId">The owner of the calling key (D3).</param>
    /// <param name="goalId">The goal.</param>
    /// <param name="targetSocPercent">The new target, or <see langword="null"/> to keep it.</param>
    /// <param name="departureUtc">The new departure (UTC), or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The goal and its current plan, or why not.</returns>
    public async Task<GoalOutcome> ChangeAsync(OwnerId ownerId, GoalId goalId, int? targetSocPercent, DateTimeOffset? departureUtc, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = time.GetUtcNow();
            if (await goals.GetAsync(ownerId, goalId, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                return new GoalOutcome.NotFound();
            }

            if (!stored.IsOpenAt(now))
            {
                return new GoalOutcome.Conflict("The goal's departure has passed, so it can no longer be changed.");
            }

            var current = await plans.GetCurrentAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (current?.Plan.StatusAsOf(now) == PlanStatus.Confirmed)
            {
                return new GoalOutcome.Conflict("The goal's plan is confirmed; it cannot be changed until its commands can be rescheduled.");
            }

            var goal = stored.Goal;
            var target = targetSocPercent ?? goal.TargetSocPercent;
            var departure = departureUtc ?? goal.DepartureUtc;
            if (CheckDeparture(departure, now) is { } departureProblem)
            {
                return departureProblem;
            }

            var resolved = await ResolveAsync(ownerId, goal.VehicleDeviceId, goal.ChargerDeviceId, target, cancellationToken).ConfigureAwait(false);
            if (resolved.Problem is { } problem)
            {
                return problem;
            }

            if (target == goal.TargetSocPercent && departure == goal.DepartureUtc)
            {
                // Same values: an ordinary re-plan, which also re-makes an expired draft.
                var outcome = await ReplanAsync(stored, PlanReason.GoalChanged, reportedSocPercent: null, cancellationToken).ConfigureAwait(false);
                var plan = outcome is ReplanOutcome.Added added ? added.Version : await plans.GetCurrentAsync(goalId, cancellationToken).ConfigureAwait(false);
                return new GoalOutcome.Planned(stored, plan!);
            }

            var changed = stored with
            {
                Goal = new Goal(goal.Id, goal.OwnerId, goal.VehicleDeviceId, goal.ChargerDeviceId, target, departure),
                UpdatedAt = now,
            };
            var inputs = resolved.Devices!;
            var result = await PlanAsync(changed, inputs, inputs.SocPercent!.Value, now, cancellationToken).ConfigureAwait(false);
            var next = Version(changed, result, inputs.SocPercent.Value, PlanReason.GoalChanged, (current?.Plan.Version ?? 0) + 1, current, now);
            if (await goals.ChangeAsync(changed, next, cancellationToken).ConfigureAwait(false) == GoalWriteOutcome.Stored)
            {
                LogReplanned(goal.Id.Value, next.Plan.Version, PlanReason.GoalChanged);
                return new GoalOutcome.Planned(changed, next);
            }

            if (attempt > 0)
            {
                return new GoalOutcome.Conflict("The goal was re-planned at the same moment; repeat the change.");
            }
        }
    }

    /// <summary>
    /// Re-plans <paramref name="goal"/> after new prices, charge drift or an unchanged goal change. Stores a new
    /// version only when the plan changed, or when an expired draft is made again by a goal change.
    /// </summary>
    /// <param name="goal">The goal.</param>
    /// <param name="reason">Why.</param>
    /// <param name="reportedSocPercent">The vehicle's reported charge for a drift re-plan; otherwise its last reading is used.</param>
    /// <param name="cancellationToken">Cancels the re-plan.</param>
    /// <returns>What happened.</returns>
    public async Task<ReplanOutcome> ReplanAsync(StoredGoal goal, PlanReason reason, decimal? reportedSocPercent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = time.GetUtcNow();
            if (!goal.IsOpenAt(now))
            {
                return Skip(goal, "the departure has passed");
            }

            if (await plans.GetCurrentAsync(goal.Goal.Id, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return Skip(goal, "the goal has no plan");
            }

            var status = current.Plan.StatusAsOf(now);
            var remakeExpired = reason == PlanReason.GoalChanged && status == PlanStatus.Expired;
            if (status == PlanStatus.Expired && !remakeExpired)
            {
                return new ReplanOutcome.Skipped("the draft has expired");
            }

            var resolved = await ResolveAsync(goal.Goal.OwnerId, goal.Goal.VehicleDeviceId, goal.Goal.ChargerDeviceId, targetSocPercent: null, cancellationToken).ConfigureAwait(false);
            if (resolved.Devices is not { } inputs)
            {
                return Skip(goal, "the vehicle or charger cannot be planned with");
            }

            if ((reportedSocPercent ?? inputs.SocPercent) is not { } soc)
            {
                return Skip(goal, "the vehicle has not reported a charge");
            }

            if (GreedyPlanner.Window(now, goal.Goal.DepartureUtc, _planner.SafetyMargin).Count == 0)
            {
                return Skip(goal, "no whole slot is left before the safety margin");
            }

            var result = await PlanAsync(goal, inputs, soc, now, cancellationToken).ConfigureAwait(false);
            if (!remakeExpired && SamePlan(result, current.Plan))
            {
                return new ReplanOutcome.Unchanged();
            }

            var next = Version(goal, result, soc, reason, current.Plan.Version + 1, current, now);
            if (await plans.AddVersionAsync(next, cancellationToken).ConfigureAwait(false))
            {
                LogReplanned(goal.Goal.Id.Value, next.Plan.Version, reason);
                return new ReplanOutcome.Added(next);
            }
        }

        return new ReplanOutcome.Unchanged();
    }

    private static bool SamePlan(PlanningResult result, Plan plan) =>
        result.Slots.SequenceEqual(plan.Slots)
        && result.ExpectedCostEur == plan.ExpectedCostEur
        && result.ArrivalCostEur == plan.ArrivalCostEur
        && result.TargetMissed == plan.TargetMissed
        && result.PricesComplete == plan.PricesComplete;

    // The departure rules of the planning-api spec, in its order: past, no whole slot, too far ahead.
    private GoalOutcome.Unprocessable? CheckDeparture(DateTimeOffset departure, DateTimeOffset now)
    {
        if (departure <= now)
        {
            return new("The departure is in the past.");
        }

        if (GreedyPlanner.Window(now, departure, _planner.SafetyMargin).Count == 0)
        {
            return new($"The departure leaves no whole 15-minute slot before the {_planner.SafetyMargin.TotalMinutes:0}-minute safety margin.");
        }

        return departure - now > _planner.MaxHorizon
            ? new($"The departure is more than {_planner.MaxHorizon.TotalHours:0} hours ahead.")
            : null;
    }

    // Reads both devices owner-scoped and checks them in the spec's order: existence (404), types, same device,
    // a reported charge, the target against it. With a null target only the devices are checked (re-plans).
    private async Task<Resolved> ResolveAsync(OwnerId ownerId, DeviceId vehicleId, DeviceId chargerId, int? targetSocPercent, CancellationToken cancellationToken)
    {
        var vehicle = await devices.GetAsync(ownerId, vehicleId, cancellationToken).ConfigureAwait(false);
        var charger = await devices.GetAsync(ownerId, chargerId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null || charger is null)
        {
            return new(new GoalOutcome.NotFound(), null);
        }

        if (vehicle.Type != DeviceType.Vehicle || vehicle.Attributes.BatteryCapacityWh is not { } capacity)
        {
            return new(new GoalOutcome.Unprocessable("The vehicle id does not name a vehicle with a known battery capacity."), null);
        }

        if (charger.Type != DeviceType.Charger || charger.Attributes.MaxPowerW is not { } power)
        {
            return new(new GoalOutcome.Unprocessable("The charger id does not name a charger with a known maximum power."), null);
        }

        if (vehicleId == chargerId)
        {
            return new(new GoalOutcome.Unprocessable("The vehicle and the charger must be different devices."), null);
        }

        var soc = vehicle.State?.LastReading?.SocPercent;
        var inputs = new DeviceInputs(capacity, power, soc);
        if (targetSocPercent is not { } target)
        {
            return new(null, inputs);
        }

        if (soc is not { } charge)
        {
            return new(new GoalOutcome.Unprocessable("The vehicle has not reported its charge yet, so the goal cannot be planned."), null);
        }

        return target < charge
            ? new(new GoalOutcome.Unprocessable($"The target {target} % is below the vehicle's current charge of {charge:0.#} %."), null)
            : new(null, inputs);
    }

    private async Task<PlanningResult> PlanAsync(StoredGoal goal, DeviceInputs inputs, decimal socPercent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var window = GreedyPlanner.Window(now, goal.Goal.DepartureUtc, _planner.SafetyMargin);
        IReadOnlyList<PriceSlot> known = window.Count == 0
            ? []
            : await prices.ReadAsync(pricesOptions.Value.Zone, window[0], window[^1] + Core.SlotGrid.SlotLength, afterUtc: null, limit: 0, cancellationToken).ConfigureAwait(false);

        return GreedyPlanner.Plan(new PlanningInput(
            now,
            goal.Goal.DepartureUtc,
            Math.Clamp(socPercent, 0m, 100m),
            goal.Goal.TargetSocPercent,
            inputs.BatteryCapacityWh,
            inputs.MaxPowerW,
            known.ToDictionary(p => p.StartUtc, p => p.PriceEurPerMwh),
            _planner.SafetyMargin,
            _planner.ChargingEfficiency));
    }

    // Created and GoalChanged give a draft; PricesPublished and ChargeDrift keep the current status, so a
    // confirmed plan stays confirmed with its confirmation time.
    private StoredPlan Version(StoredGoal goal, PlanningResult result, decimal socPercent, PlanReason reason, int version, StoredPlan? current, DateTimeOffset now)
    {
        var keepConfirmed = reason is PlanReason.PricesPublished or PlanReason.ChargeDrift
            && current?.Plan.Status == PlanStatus.Confirmed;
        var status = keepConfirmed ? PlanStatus.Confirmed : PlanStatus.Draft;
        var plan = new Plan(
            goal.PlanId,
            goal.Goal.Id,
            goal.Goal.OwnerId,
            version,
            status,
            result.Slots,
            result.ExpectedCostEur,
            result.ArrivalCostEur,
            result.TargetMissed,
            result.PricesComplete,
            now,
            status == PlanStatus.Draft ? now + _planner.DraftExpiry : null);
        return new StoredPlan(plan, reason, socPercent, goal.Goal.TargetSocPercent, result.EnergyNeededWh, keepConfirmed ? current!.ConfirmedAt : null);
    }

    private ReplanOutcome.Skipped Skip(StoredGoal goal, string reason)
    {
        LogSkipped(goal.Goal.Id.Value, reason);
        return new ReplanOutcome.Skipped(reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Goal {GoalId} re-planned: version {Version} ({Reason})")]
    private partial void LogReplanned(string goalId, int version, PlanReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Goal {GoalId} not re-planned: {Reason}")]
    private partial void LogSkipped(string goalId, string reason);

    private sealed record DeviceInputs(long BatteryCapacityWh, int MaxPowerW, decimal? SocPercent);

    private sealed record Resolved(GoalOutcome? Problem, DeviceInputs? Devices);
}
