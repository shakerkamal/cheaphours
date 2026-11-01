using System.Globalization;

namespace CheapHours.Simulators.CarCloud;

/// <summary>
/// A stand-in for a car maker's cloud: OAuth 2.0 tokens that expire and rotate, a per-account rate limit,
/// vehicles whose charge rises over time and idempotent charging commands. Tests build it in-process with
/// their own clock; <c>Program.cs</c> only runs it. The API is the one in the car-cloud provider's README.
/// </summary>
public static class CarCloudSimulator
{
    /// <summary>Builds the simulator application.</summary>
    /// <param name="args">The process arguments (configuration overrides).</param>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    /// <returns>The application, not yet started.</returns>
    /// <exception cref="InvalidOperationException">The client secret or an account's seed refresh token is missing.</exception>
    public static WebApplication Build(string[] args, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var options = builder.Configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>() ?? new SimulatorOptions();
        if (options.Accounts.Count == 0)
        {
            options.Accounts.Add(new SimulatorOptions.AccountOptions());
        }

        Validate(options);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SimulatorState>();

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapPost("/oauth/token", TokenAsync).DisableAntiforgery();

        var api = app.MapGroup("/api/v1").AddEndpointFilter(AuthorizeAndThrottle);
        api.MapGet("/vehicles", (HttpContext http, SimulatorState state) => Results.Json(new { vehicles = state.Vehicles(Account(http)) }));
        api.MapGet("/vehicles/{id}/state", (string id, HttpContext http, SimulatorState state) =>
            state.State(Account(http), id) is { } s ? Results.Json(s) : Error(404, "unknown_vehicle"));
        // Two plain routes: the slim builder has no regex route constraint.
        api.MapPost("/vehicles/{id}/charging/start", (string id, HttpContext http, SimulatorState state) => Command(id, "start", http, state));
        api.MapPost("/vehicles/{id}/charging/stop", (string id, HttpContext http, SimulatorState state) => Command(id, "stop", http, state));

        if (app.Configuration.GetValue<bool>("Admin:Enabled"))
        {
            var admin = app.MapGroup("/admin");
            admin.MapPost("/vehicles/{id}/plug-in", (string id, SimulatorState state) => state.PlugIn(id) ? Results.NoContent() : Results.NotFound());
            admin.MapPost("/vehicles/{id}/unplug", (string id, SimulatorState state) => state.Unplug(id) ? Results.NoContent() : Results.NotFound());
            admin.MapPost("/vehicles/{id}/start", (string id, SimulatorState state) => state.StartFromCar(id) switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Error(422, "not_plugged_in"),
            });
            admin.MapPost("/accounts/{id}/revoke", (string id, SimulatorState state) => state.Revoke(id) ? Results.NoContent() : Results.NotFound());
        }

        return app;
    }

    private static void Validate(SimulatorOptions options)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            missing.Add("Simulator:ClientSecret");
        }

        for (var i = 0; i < options.Accounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(options.Accounts[i].RefreshToken))
            {
                missing.Add(string.Create(CultureInfo.InvariantCulture, $"Simulator:Accounts:{i}:RefreshToken"));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException("The car-cloud simulator needs: " + string.Join(", ", missing) + ".");
        }
    }

    private static async Task<IResult> TokenAsync(HttpRequest request, SimulatorState state)
    {
        if (!request.HasFormContentType)
        {
            return Error(400, "invalid_request");
        }

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
        var result = state.Refresh(form["client_id"], form["client_secret"], form["grant_type"], form["refresh_token"]);
        if (result.Error is not null)
        {
            return Error(result.Status, result.Error);
        }

        var lifetime = request.HttpContext.RequestServices.GetRequiredService<SimulatorOptions>().AccessTokenLifetime;
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = result.AccessToken!,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)lifetime.TotalSeconds,
            ["refresh_token"] = result.RefreshToken!,
        });
    }

    private static async ValueTask<object?> AuthorizeAndThrottle(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var state = http.RequestServices.GetRequiredService<SimulatorState>();
        var header = http.Request.Headers.Authorization.ToString();
        var account = header.StartsWith("Bearer ", StringComparison.Ordinal) ? state.Authenticate(header["Bearer ".Length..]) : null;
        if (account is null)
        {
            return Error(401, "invalid_token");
        }

        if (state.Throttle(account) is { } retryAfter)
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Error(429, "rate_limited");
        }

        http.Items[nameof(Account)] = account;
        return await next(context).ConfigureAwait(false);
    }

    private static IResult Command(string id, string action, HttpContext http, SimulatorState state)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Error(400, "missing_idempotency_key");
        }

        var result = state.Command(Account(http), id, action, key);
        return Results.Json(result.Body, statusCode: result.Status);
    }

    private static string Account(HttpContext http) => (string)http.Items[nameof(Account)]!;

    private static IResult Error(int status, string error) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error }, statusCode: status);
}
