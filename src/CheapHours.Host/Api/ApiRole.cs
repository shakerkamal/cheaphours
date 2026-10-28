using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Api.OpenApi;
using CheapHours.Host.Health;
using CheapHours.Messaging.Contracts.Json;
using CheapHours.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;

namespace CheapHours.Host.Api;

/// <summary>Builds the public REST API process.</summary>
internal static class ApiRole
{
    private const string ReadyTag = "ready";

    /// <summary>
    /// Creates the API application: <c>/v1</c> endpoints, API-key auth backed by MongoDB, Problem Details,
    /// OpenAPI, a background startup (indexes, key seeding) and the health endpoints.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configured, not yet started, application.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(options => CheapHoursJson.Configure(options.SerializerOptions));
        builder.Services.AddCheapHoursProblemDetails();
        builder.Services.Configure<ExceptionHandlerOptions>(options =>
            // Minimal APIs throw BadHttpRequestException for binding failures in Development; keep its 4xx status.
            options.StatusCodeSelector = exception => exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        builder.Services.AddApiKeyAuthentication(builder.Configuration);
        builder.Services.AddCheapHoursOpenApi();

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddMongoPersistence(builder.Configuration);
        builder.Services.AddSingleton<ApiState>();
        builder.Services.AddHostedService<ApiStartup>();
        builder.Services.AddHealthChecks()
            .AddCheck<ApiReadinessHealthCheck>("api", tags: [ReadyTag])
            .AddCheck<MongoPingHealthCheck>("mongo", tags: [ReadyTag]);

        var app = builder.Build();

        // Explicit order: the exception handler must wrap authentication, so a failing key store is a clean 500.
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOpenApi("/openapi/{documentName}.json");
        if (app.Environment.IsDevelopment())
        {
            // Interactive reference page for reviewers; not part of the contract (excluded from the document).
            app.MapScalarApiReference();
        }

        // Health endpoints are for the orchestrator, not clients: no key, and not in the OpenAPI document.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).ExcludeFromDescription();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).ExcludeFromDescription();

        app.MapV1Endpoints();

        return app;
    }
}
