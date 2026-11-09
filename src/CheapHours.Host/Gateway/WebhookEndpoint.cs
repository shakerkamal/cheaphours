using CheapHours.Core.Devices;
using CheapHours.Host.Devices;
using CheapHours.Host.Observability;
using CheapHours.Messaging;
using CheapHours.Providers.Kit;
using MongoDB.Driver;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Maps <c>POST /webhooks/{providerId}/{externalAccountId}</c> (design §1). The checks run in a fixed order: readiness,
/// provider, size, signature, account, translation, recording. The signature is checked before the body is read as
/// JSON and before the account is looked up, so an unsigned caller learns nothing. The vendor is answered only after
/// the readings are published; every status code tells it whether to retry. Logs carry the provider, the account and
/// event counts, never the body or the headers.
/// </summary>
internal static partial class WebhookEndpoint
{
    /// <summary>The route.</summary>
    public const string Route = "/webhooks/{providerId}/{externalAccountId}";

    /// <summary>The largest body accepted.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Maps the webhook endpoint.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapWebhookEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Route, (RequestDelegate)HandleAsync);

    private static async Task HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebhookEndpoint).FullName!);
        var providerId = (string)context.Request.RouteValues["providerId"]!;
        var externalAccountId = (string)context.Request.RouteValues["externalAccountId"]!;
        var receivedAt = services.GetRequiredService<TimeProvider>().GetUtcNow();

        // 1. Starting or draining: the vendor retries later.
        if (!services.GetRequiredService<GatewayState>().AcceptsConnections)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // 2. A registered provider that verifies webhooks (and so, by the registration rule, translates them).
        if (services.GetServices<IWebhookVerifier>().FirstOrDefault(v => string.Equals(v.ProviderId, providerId, StringComparison.Ordinal)) is not { } verifier
            || verifier is not IInboundTranslator translator)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // From here every log entry names the (registered) provider; the ingestor adds each device's id.
        using var scope = LogScopes.ForDevice(logger, verifier.ProviderId, externalId: null);

        // 3. At most 64 KB, by header and while reading.
        var body = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (body is null)
        {
            LogTooLarge(logger, providerId);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        // 4. The signature, over the raw bytes, before anything reads them.
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var message = new InboundMessage(externalId: null, body, headers, receivedAt);
        if (!verifier.Verify(message))
        {
            LogBadSignature(logger, providerId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // 5. The linked account and its owner.
        var directory = services.GetService<WebhookAccountDirectory>();
        if (directory?.FindOwner(providerId, externalAccountId) is not { } owner || directory.ReportInterval(providerId) is not { } reportInterval)
        {
            LogUnknownAccount(logger, providerId, externalAccountId);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 6. Translation; a body the provider cannot read is refused, so the vendor sees the problem.
        var events = translator.Translate(message);
        if (events is [DeviceEvent.MalformedInput malformed])
        {
            LogMalformed(logger, providerId, externalAccountId, malformed.Reason);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 7. Recording: stored and published before the answer. A broker or database outage asks for a retry.
        try
        {
            await services.GetRequiredService<DeviceEventIngestor>()
                .IngestAsync(providerId, verifier.Capabilities, owner, reportInterval, events, receivedAt, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BrokerUnavailableException or MongoException or TimeoutException)
        {
            LogRecordFailed(logger, providerId, externalAccountId, ex.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // 8. Accepted.
        LogAccepted(logger, providerId, externalAccountId, events.Count);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    /// <summary>Reads the body into memory, or returns <see langword="null"/> once it is larger than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a {ProviderId} webhook: the body is larger than 64 KB")]
    private static partial void LogTooLarge(ILogger logger, string providerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a {ProviderId} webhook: the signature is missing, invalid or outside the time window")]
    private static partial void LogBadSignature(ILogger logger, string providerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a signed {ProviderId} webhook for account {ExternalAccountId}: the account is not configured")]
    private static partial void LogUnknownAccount(ILogger logger, string providerId, string externalAccountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a {ProviderId} webhook for account {ExternalAccountId}: {Reason}")]
    private static partial void LogMalformed(ILogger logger, string providerId, string externalAccountId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record a {ProviderId} webhook for account {ExternalAccountId} ({Error}); answered 503 so the vendor retries")]
    private static partial void LogRecordFailed(ILogger logger, string providerId, string externalAccountId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted a {ProviderId} webhook for account {ExternalAccountId} with {EventCount} event(s)")]
    private static partial void LogAccepted(ILogger logger, string providerId, string externalAccountId, int eventCount);
}
