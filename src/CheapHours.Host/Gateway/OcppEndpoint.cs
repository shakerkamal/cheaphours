using System.Net.WebSockets;
using CheapHours.Providers.Ocpp;

namespace CheapHours.Host.Gateway;

/// <summary>Maps <c>/ocpp/{chargePointId}</c>: every check runs before the WebSocket upgrade.</summary>
internal static partial class OcppEndpoint
{
    /// <summary>The route.</summary>
    public const string Route = "/ocpp/{chargePointId}";

    /// <summary>The only subprotocol accepted.</summary>
    public const string SubProtocol = "ocpp1.6";

    /// <summary>The challenge sent with every 401.</summary>
    public const string Challenge = "Basic realm=\"ocpp\", charset=\"UTF-8\"";

    /// <summary>Maps the charger endpoint.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapOcppEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.Map(Route, (HttpContext context, string chargePointId) => HandleAsync(context, chargePointId));

    private static async Task HandleAsync(HttpContext context, string chargePointId)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OcppEndpoint).FullName!);

        // 1. Draining or not yet initialised: try another replica, or again shortly.
        if (!services.GetRequiredService<GatewayState>().AcceptsConnections)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // 2. The id is checked before it is logged or looked up.
        if (!OcppChargePointId.IsValid(chargePointId))
        {
            LogInvalidId(logger);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 3. Only WebSocket upgrades.
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 4. Basic auth against the registration. Every failure gets the same response.
        var auth = await services.GetRequiredService<ChargerAuthenticator>()
            .AuthenticateAsync(chargePointId, context.Request.Headers.Authorization.ToString(), context.RequestAborted)
            .ConfigureAwait(false);
        if (!auth.Succeeded)
        {
            LogRefused(logger, chargePointId, auth.FailureReason!);
            context.Response.Headers.WWWAuthenticate = Challenge;
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // 5. The charger must speak OCPP 1.6J.
        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(SubProtocol, StringComparer.Ordinal))
        {
            LogNoSubProtocol(logger, chargePointId);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 6. Accept, then run the connection until it ends.
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = SubProtocol }).ConfigureAwait(false);
        await services.GetRequiredService<ChargerSession>().RunAsync(socket, auth.Registration!, context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a charger connection: the charge point id is not valid")]
    private static partial void LogInvalidId(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused charger {ChargePointId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string chargePointId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused charger {ChargePointId}: it did not offer the ocpp1.6 subprotocol")]
    private static partial void LogNoSubProtocol(ILogger logger, string chargePointId);
}
