using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api.Dtos;

/// <summary>The body of <c>POST /v1/goals</c>.</summary>
/// <param name="VehicleDeviceId">The vehicle to charge.</param>
/// <param name="ChargerDeviceId">The charger to command.</param>
/// <param name="TargetSocPercent">The target state of charge, 1–100.</param>
/// <param name="DepartureAt">When the car must be ready (UTC).</param>
internal sealed record CreateGoalRequest(DeviceId VehicleDeviceId, DeviceId ChargerDeviceId, int TargetSocPercent, DateTimeOffset DepartureAt);

/// <summary>A charging goal.</summary>
/// <param name="Id">The goal id.</param>
/// <param name="VehicleDeviceId">The vehicle to charge.</param>
/// <param name="ChargerDeviceId">The charger to command.</param>
/// <param name="TargetSocPercent">The target state of charge.</param>
/// <param name="DepartureAt">When the car must be ready (UTC).</param>
internal sealed record GoalDto(GoalId Id, DeviceId VehicleDeviceId, DeviceId ChargerDeviceId, int TargetSocPercent, DateTimeOffset DepartureAt);

/// <summary>The response to <c>POST /v1/goals</c>: the goal and its first draft plan.</summary>
/// <param name="Goal">The created goal.</param>
/// <param name="Plan">Its draft plan; confirm it before it expires.</param>
internal sealed record GoalCreatedResponse(GoalDto Goal, PlanDto Plan);

/// <summary>A charging plan version.</summary>
/// <param name="Id">The plan id.</param>
/// <param name="GoalId">The goal it serves.</param>
/// <param name="Version">The plan version; re-planning creates a new one.</param>
/// <param name="Status">Draft, confirmed, superseded or expired.</param>
/// <param name="Slots">The chosen 15-minute slots.</param>
/// <param name="ExpectedCostEur">The plan's expected cost in EUR (exchange price only).</param>
/// <param name="ArrivalCostEur">The cost of charging on arrival instead, in EUR.</param>
/// <param name="SavingsEur">The difference, in EUR.</param>
/// <param name="TargetMissed">Whether the available slots cannot reach the target before departure.</param>
/// <param name="ExpiresAt">When a draft expires (UTC); absent once confirmed.</param>
internal sealed record PlanDto(
    PlanId Id,
    GoalId GoalId,
    int Version,
    PlanStatus Status,
    IReadOnlyList<PlanSlotDto> Slots,
    decimal ExpectedCostEur,
    decimal ArrivalCostEur,
    decimal SavingsEur,
    bool TargetMissed,
    DateTimeOffset? ExpiresAt);

/// <summary>One slot of a plan.</summary>
/// <param name="Start">Slot start (UTC).</param>
/// <param name="End">Slot end (UTC).</param>
/// <param name="PowerW">Charging power during the slot in W.</param>
internal sealed record PlanSlotDto(DateTimeOffset Start, DateTimeOffset End, int PowerW);

/// <summary>The response to <c>POST /v1/plans/{id}/confirm</c>: the confirmed plan and the commands it created.</summary>
/// <param name="Plan">The confirmed plan.</param>
/// <param name="Commands">The created commands; follow each at <c>/v1/commands/{id}</c>.</param>
internal sealed record PlanConfirmedResponse(PlanDto Plan, IReadOnlyList<CommandAcceptedResponse> Commands);
