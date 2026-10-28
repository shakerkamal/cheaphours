using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Devices;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace CheapHours.Host.Api;

/// <summary>
/// The ten <c>/v1</c> endpoints. Their contract (paths, scopes, parameters, response types) is fixed here;
/// built endpoints point at their handlers, the others answer 501 until the feature named in their stub builds them.
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
        devices.MapPost("/{id}/commands", SendCommand).WithName("SendDeviceCommand").WithSummary("Send one command directly.")
            .RequireAuthorization(ApiScopes.ExecutePolicy).Produces<CommandAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        v1.MapGet("/prices", ListPrices).WithTags("Prices").WithName("ListPrices").WithSummary("Day-ahead price slots in EUR/MWh.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<Page<PriceSlotDto>>().ProducesProblem(StatusCodes.Status400BadRequest);

        var goals = v1.MapGroup("/goals").WithTags("Goals and plans");
        goals.MapPost("/", CreateGoal).WithName("CreateGoal").WithSummary("Set a goal; returns a draft plan.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<GoalCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        goals.MapGet("/{id}/plan", GetPlan).WithName("GetGoalPlan").WithSummary("The goal's current plan with cost and savings.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<PlanDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapPost("/plans/{id}/confirm", ConfirmPlan).WithTags("Goals and plans").WithName("ConfirmPlan").WithSummary("Turn a draft plan into commands.")
            .RequireAuthorization(ApiScopes.ExecutePolicy).Produces<PlanConfirmedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status410Gone);

        v1.MapGet("/commands/{id}", GetCommand).WithTags("Commands").WithName("GetCommand").WithSummary("A command's state and history.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<CommandDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapGet("/savings", GetSavings).WithTags("Savings").WithName("GetSavings").WithSummary("Savings over a period, against charging on arrival.")
            .RequireAuthorization(ApiScopes.ReadPolicy).Produces<SavingsDto>().ProducesProblem(StatusCodes.Status400BadRequest);

        return v1;
    }

    private static IResult SendCommand(DeviceId id, CreateCommandRequest body, [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey) => NotYet("command dispatch");

    private static IResult ListPrices(string zone, DateTimeOffset from, DateTimeOffset to, string? cursor, int? limit) => NotYet("the prices and planner feature");

    private static IResult CreateGoal(CreateGoalRequest body, [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey) => NotYet("the prices and planner feature");

    private static IResult GetPlan(GoalId id) => NotYet("the prices and planner feature");

    private static IResult ConfirmPlan(PlanId id, [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey) => NotYet("command dispatch");

    private static IResult GetCommand(CommandId id) => NotYet("command dispatch");

    private static IResult GetSavings(DateTimeOffset from, DateTimeOffset to) => NotYet("the prices and planner feature");

    private static IResult NotYet(string feature) =>
        ErrorCatalogue.NotImplemented.ToResult($"This endpoint is not built yet; it arrives with {feature}.");
}
