namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>
/// A stand-in for a heat-pump maker's cloud (design §6): every interval it posts a signed <c>device.state</c> webhook per
/// heat pump to the configured callback URL, retrying failed deliveries with the same body, and admin endpoints drive
/// the demo. It references no CheapHours project, like an outside vendor.
/// </summary>
public static class HeatPumpCloudSimulator
{
    /// <summary>Builds the simulator.</summary>
    /// <param name="args">The arguments; configuration keys as <c>--Simulator:CallbackUrl=…</c>.</param>
    /// <param name="time">The clock; the system clock when <see langword="null"/>.</param>
    /// <param name="handler">The handler deliveries go through; lets tests receive them in process.</param>
    /// <returns>The application, not started.</returns>
    /// <exception cref="InvalidOperationException">The callback URL or the signing secret is missing; the message names the settings only.</exception>
    public static WebApplication Build(string[] args, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var options = builder.Configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>() ?? new SimulatorOptions();
        if (options.HeatPumps.Count == 0)
        {
            options.HeatPumps.Add(new SimulatorOptions.HeatPumpOptions());
        }

        Validate(options);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SimulatorState>();
        builder.Services.AddSingleton(_ => new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<WebhookSender>();
        builder.Services.AddHostedService<DeliveryService>();

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());

        if (app.Configuration.GetValue<bool>("Admin:Enabled"))
        {
            var admin = app.MapGroup("/admin");
            admin.MapPost("/heat-pumps/{id}/redeliver", async (string id, SimulatorState state, WebhookSender sender, HttpContext http) =>
                state.LastEvent(id) is { } last ? Results.Json(new { eventId = last.EventId, statusCode = await sender.DeliverAsync(last, http.RequestAborted).ConfigureAwait(false) }) : Results.NotFound());
            admin.MapPost("/heat-pumps/{id}/mode/{mode}", (string id, string mode, SimulatorState state) => state.SetMode(id, mode) switch
            {
                null => Results.NotFound(),
                false => Results.Json(new { error = "unknown_mode", modes = SimulatorState.Modes.Order(StringComparer.Ordinal) }, statusCode: 400),
                true => Results.NoContent(),
            });
            admin.MapPost("/heat-pumps/{id}/remove", async (string id, SimulatorState state, WebhookSender sender, HttpContext http) =>
                state.Remove(id) is { } deleted ? Results.Json(new { eventId = deleted.EventId, statusCode = await sender.DeliverAsync(deleted, http.RequestAborted).ConfigureAwait(false) }) : Results.NotFound());
            admin.MapGet("/deliveries", (SimulatorState state) => Results.Json(new { deliveries = state.Deliveries() }));
        }

        return app;
    }

    private static void Validate(SimulatorOptions options)
    {
        var missing = new List<string>();
        if (options.CallbackUrl is not { IsAbsoluteUri: true })
        {
            missing.Add("Simulator:CallbackUrl (an absolute URL)");
        }

        if (string.IsNullOrWhiteSpace(options.SigningSecret))
        {
            missing.Add("Simulator:SigningSecret");
        }

        if (options.Interval <= TimeSpan.Zero || options.AttemptTimeout <= TimeSpan.Zero)
        {
            missing.Add("Simulator:Interval and Simulator:AttemptTimeout (positive)");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException("The heat-pump-cloud simulator needs: " + string.Join(", ", missing) + ".");
        }
    }
}
