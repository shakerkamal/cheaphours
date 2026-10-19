using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Api.OpenApi;
using CheapHours.Messaging.Contracts.Json;
using Scalar.AspNetCore;

namespace CheapHours.Host.Api;

/// <summary>Builds the public REST API process.</summary>
internal static class ApiRole
{
    /// <summary>Creates the API application: <c>/v1</c> endpoints, API-key auth, Problem Details and OpenAPI.</summary>
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

        app.MapV1Endpoints();

        return app;
    }
}
