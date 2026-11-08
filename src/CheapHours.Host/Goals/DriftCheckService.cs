using CheapHours.Core.Goals;
using CheapHours.Host.Worker;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.Plans;
using CheapHours.Planning;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Goals;

/// <summary>
/// Re-plans confirmed plans whose vehicle charges slower or faster than planned (design §6). Every check
/// interval, under the <c>plan-drift</c> lease, it compares each confirmed plan's expected charge at the time of
/// the vehicle's latest reading with the reported charge; a difference of at least the threshold re-plans the
/// goal from the reported charge as a new confirmed version. Drafts, readings without a charge and readings no
/// newer than the plan are ignored, so the version a drift re-plan made is not re-planned again by the same
/// reading.
/// </summary>
internal sealed partial class DriftCheckService(
    [FromKeyedServices(LeaseNames.PlanDrift)] ILease lease,
    IGoalStore goals,
    IPlanStore plans,
    IDeviceReader devices,
    GoalPlanningService planning,
    WorkerState state,
    IOptions<PlannerOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DriftCheckService> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly PlannerOptions _options = options.Value;
    private readonly string _holder = ReplicaIds.From(configuration);

    /// <summary>Runs one check if this replica can take the lease.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when this replica ran the check.</returns>
    public Task<bool> CheckOnceAsync(CancellationToken cancellationToken) =>
        LeasedRun.RunAsync(lease, _holder, LeaseDuration, time, CheckAsync, cancellationToken);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.DriftCheckInterval, time, stoppingToken).ConfigureAwait(false);
                try
                {
                    await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogCheckFailed(ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        foreach (var goal in await goals.ListOpenAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            // The running version charges, even while a change draft waits above it.
            if (await plans.GetRunningAsync(goal.Goal.Id, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                continue;
            }

            var vehicle = await devices.GetAsync(goal.Goal.OwnerId, goal.Goal.VehicleDeviceId, cancellationToken).ConfigureAwait(false);
            if (vehicle?.State?.LastReading is not { SocPercent: { } reported } reading || reading.MeasurementTime <= current.Plan.MadeAtUtc)
            {
                continue;
            }

            var expected = PlanProgress.ExpectedSoc(current.Plan.Slots, current.StartSocPercent, current.TargetSocPercent, current.EnergyNeededWh, reading.MeasurementTime);
            if (Math.Abs(reported - expected) < _options.DriftThresholdPercent)
            {
                continue;
            }

            LogDrift(goal.Goal.Id.Value, reported, expected);
            await planning.ReplanAsync(goal, PlanReason.ChargeDrift, reported, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Goal {GoalId}: vehicle reports {Reported} % where the plan expects {Expected:0.#} %; re-planning")]
    private partial void LogDrift(string goalId, decimal reported, decimal expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drift check failed ({ExceptionType}); the next check tries again")]
    private partial void LogCheckFailed(string exceptionType);
}
