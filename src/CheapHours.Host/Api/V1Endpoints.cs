using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Commands;
using CheapHours.Host.Api.Devices;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Api.Goals;
using CheapHours.Host.Api.Idempotency;
using CheapHours.Host.Api.Prices;
using CheapHours.Host.Api.Savings;
using Microsoft.AspNetCore.Mvc;

namespace CheapHours.Host.Api;

/// <summary>
/// The eleven <c>/v1</c> endpoints. Their contract (paths, scopes, parameters, response types) is fixed here, and
/// each points at its handler.
/// </summary>
internal static class V1Endpoints
{
    /// <summary>The header that makes a <c>POST</c> safe to repeat.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Maps every <c>/v1</c> endpoint.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The <c>/v1</c> route group.</returns>
    public static RouteGroupBuilder MapV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");

        var devices = v1.MapGroup("/devices").WithTags("Devices");
        devices.MapGet("/", DeviceEndpoints.ListAsync).WithName("ListDevices").WithSummary("List devices, with cursor paging.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<Page<DeviceDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        devices.MapGet("/{id}", DeviceEndpoints.GetAsync).WithName("GetDevice").WithSummary("One device and its last reading.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<DeviceDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        devices.MapGet("/{id}/telemetry", DeviceEndpoints.TelemetryAsync).WithName("GetDeviceTelemetry").WithSummary("Readings over time, in 15-minute buckets.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<Page<TelemetryBucketDto>>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        devices.MapPost("/{id}/commands", CommandEndpoints.SendAsync).Idempotent().WithName("SendDeviceCommand").WithSummary("Send one command directly.")
            .Accepts<CreateCommandRequest>("application/json")
            .RequireAuthorization(ApiScopes.ExecutePolicy).Produces<CommandAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        v1.MapGet("/prices", PriceEndpoints.ListAsync).WithTags("Prices").WithName("ListPrices").WithSummary("Day-ahead price slots in EUR/MWh.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<Page<PriceSlotDto>>().ProducesProblem(StatusCodes.Status400BadRequest);

        var goals = v1.MapGroup("/goals").WithTags("Goals and plans");
        goals.MapPost("/", GoalEndpoints.CreateAsync).Idempotent().WithName("CreateGoal").WithSummary("Set a goal; returns a draft plan.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<GoalCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        goals.MapPatch("/{id}", GoalEndpoints.UpdateAsync).WithName("UpdateGoal").WithSummary("Change a goal's target or departure; returns a new draft plan.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<GoalUpdatedResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        goals.MapGet("/{id}/plan", GoalEndpoints.GetPlanAsync).WithName("GetGoalPlan").WithSummary("The goal's current plan with cost and savings.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<PlanDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapPost("/plans/{id}/confirm", GoalEndpoints.ConfirmAsync).Idempotent().WithTags("Goals and plans").WithName("ConfirmPlan").WithSummary("Turn a draft plan into commands.")
            .RequireAuthorization(ApiScopes.ExecutePolicy).Produces<PlanConfirmedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status410Gone);

        v1.MapGet("/commands/{id}", CommandEndpoints.GetAsync).WithTags("Commands").WithName("GetCommand").WithSummary("A command's state and history.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<CommandDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapGet("/savings", SavingsEndpoints.GetAsync).WithTags("Savings").WithName("GetSavings").WithSummary("Savings over a period, against charging on arrival.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<SavingsDto>().ProducesProblem(StatusCodes.Status400BadRequest);

        return v1;
    }
}
