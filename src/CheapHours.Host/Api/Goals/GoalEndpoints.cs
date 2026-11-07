using System.Security.Claims;
using CheapHours.Core.Ids;
using CheapHours.Host.Api.Devices;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Goals;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using Microsoft.AspNetCore.Mvc;

namespace CheapHours.Host.Api.Goals;

/// <summary>
/// Handlers for setting, changing and reading a charging goal (planning-api spec). The body is checked here;
/// everything that needs the devices, the goal or the plan is decided by <see cref="GoalPlanningService"/>,
/// whose outcome maps to exactly one status code.
/// </summary>
internal static class GoalEndpoints
{
    /// <summary><c>POST /v1/goals</c>: creates a goal and its first draft plan.</summary>
    /// <param name="body">The goal.</param>
    /// <param name="idempotencyKey">The <c>Idempotency-Key</c> header; the idempotency middleware handles it.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="planning">The goal planning service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>201 with the goal and its plan, or 400, 404, 409, 422.</returns>
    public static async Task<IResult> CreateAsync(
        CreateGoalRequest body,
        [FromHeader(Name = V1Endpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        GoalPlanningService planning,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.VehicleDeviceId == default)
        {
            errors["vehicleDeviceId"] = ["Required: the id of the vehicle to charge."];
        }

        if (body.ChargerDeviceId == default)
        {
            errors["chargerDeviceId"] = ["Required: the id of the charger to use."];
        }

        CheckTarget(body.TargetSocPercent, errors);
        CheckDeparture(body.DepartureAt, errors);
        if (errors.Count > 0)
        {
            return ErrorCatalogue.ValidationFailed(errors);
        }

        var request = new GoalRequest(body.VehicleDeviceId, body.ChargerDeviceId, body.TargetSocPercent, body.DepartureAt);
        return await planning.CreateAsync(DeviceEndpoints.Owner(user), request, cancellationToken).ConfigureAwait(false) switch
        {
            GoalOutcome.Planned planned => TypedResults.Created(
                $"/v1/goals/{planned.Goal.Goal.Id}/plan",
                new GoalCreatedResponse(PlanMapping.ToDto(planned.Goal), PlanMapping.ToDto(planned.Plan, time.GetUtcNow()))),
            var other => Problem(other),
        };
    }

    /// <summary><c>PATCH /v1/goals/{id}</c>: changes an open goal's target or departure and re-plans it.</summary>
    /// <param name="id">The goal.</param>
    /// <param name="body">The values to change.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="planning">The goal planning service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with the goal and its new plan, or 400, 404, 409, 422.</returns>
    public static async Task<IResult> UpdateAsync(
        GoalId id,
        UpdateGoalRequest body,
        ClaimsPrincipal user,
        GoalPlanningService planning,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.TargetSocPercent is null && body.DepartureAt is null)
        {
            errors["body"] = ["Send targetSocPercent, departureAt or both."];
        }

        if (body.TargetSocPercent is { } target)
        {
            CheckTarget(target, errors);
        }

        if (body.DepartureAt is { } departure)
        {
            CheckDeparture(departure, errors);
        }

        if (errors.Count > 0)
        {
            return ErrorCatalogue.ValidationFailed(errors);
        }

        return await planning.ChangeAsync(DeviceEndpoints.Owner(user), id, body.TargetSocPercent, body.DepartureAt, cancellationToken).ConfigureAwait(false) switch
        {
            GoalOutcome.Planned planned => TypedResults.Ok(
                new GoalUpdatedResponse(PlanMapping.ToDto(planned.Goal), PlanMapping.ToDto(planned.Plan, time.GetUtcNow()))),
            var other => Problem(other),
        };
    }

    /// <summary><c>GET /v1/goals/{id}/plan</c>: the goal's current plan version.</summary>
    /// <param name="id">The goal.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="goals">The goal store.</param>
    /// <param name="plans">The plan store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with the plan, or 404.</returns>
    public static async Task<IResult> GetPlanAsync(
        GoalId id,
        ClaimsPrincipal user,
        IGoalStore goals,
        IPlanStore plans,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await goals.GetAsync(DeviceEndpoints.Owner(user), id, cancellationToken).ConfigureAwait(false) is null
            || await plans.GetCurrentAsync(id, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return GoalNotFound();
        }

        return TypedResults.Ok(PlanMapping.ToDto(plan, time.GetUtcNow()));
    }

    private static void CheckTarget(int target, Dictionary<string, string[]> errors)
    {
        if (target is < 1 or > 100)
        {
            errors["targetSocPercent"] = ["Must be between 1 and 100."];
        }
    }

    private static void CheckDeparture(DateTimeOffset departure, Dictionary<string, string[]> errors)
    {
        if (departure == default)
        {
            errors["departureAt"] = ["Required: when the car must be ready, in UTC."];
        }
        else if (departure.Offset != TimeSpan.Zero)
        {
            errors["departureAt"] = ["Must be a UTC time ending in 'Z'."];
        }
    }

    private static IResult GoalNotFound() =>
        ErrorCatalogue.NotFound.ToResult("No goal with this id exists for this API key.");

    private static IResult Problem(GoalOutcome outcome) => outcome switch
    {
        GoalOutcome.NotFound => ErrorCatalogue.NotFound.ToResult("The goal, the vehicle or the charger does not exist for this API key."),
        GoalOutcome.Conflict conflict => ErrorCatalogue.Conflict.ToResult(conflict.Detail),
        GoalOutcome.Unprocessable unprocessable => ErrorCatalogue.Unprocessable.ToResult(unprocessable.Detail),
        _ => throw new InvalidOperationException("Unknown goal outcome."),
    };
}
