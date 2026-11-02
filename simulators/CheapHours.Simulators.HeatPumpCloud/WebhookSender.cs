using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>
/// Delivers webhooks at least once, like a real vendor: every attempt is signed afresh over the same body
/// (<c>HeatPump-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 over "&lt;t&gt;.&lt;body&gt;"&gt;</c>). A 5xx, 408 or 429
/// answer, a timeout or a connection error is retried after 1, 2, 4 and 8 seconds on the injected clock, up to five
/// attempts; any other answer ends the delivery.
/// </summary>
internal sealed partial class WebhookSender(HttpClient client, SimulatorOptions options, SimulatorState state, TimeProvider time, ILogger<WebhookSender> logger)
{
    /// <summary>The most attempts per delivery.</summary>
    public const int MaxAttempts = 5;

    private readonly byte[] _secret = Encoding.UTF8.GetBytes(options.SigningSecret!);

    /// <summary>The <c>HeatPump-Signature</c> value for a body signed at <paramref name="signedAt"/>.</summary>
    public static string Signature(byte[] secret, DateTimeOffset signedAt, byte[] body)
    {
        var t = signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var prefix = Encoding.ASCII.GetBytes(t + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload, prefix.Length);
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(secret, payload))}";
    }

    /// <summary>Delivers one event, retrying as the vendor would.</summary>
    /// <returns>The last answer's status code, or <see langword="null"/> when no attempt got one.</returns>
    public async Task<int?> DeliverAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        int? status = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 2)), time, cancellationToken).ConfigureAwait(false);
            }

            string? error = null;
            try
            {
                status = await AttemptAsync(webhookEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                status = null;
                error = "timeout";
            }
            catch (HttpRequestException ex)
            {
                status = null;
                error = ex.HttpRequestError.ToString();
            }

            state.Log(webhookEvent, attempt, status, error);
            LogAttempt(webhookEvent.Type, webhookEvent.EventId, attempt, status, error);
            if (!ShouldRetry(status))
            {
                return status;
            }
        }

        LogGaveUp(webhookEvent.EventId, MaxAttempts);
        return status;
    }

    private static bool ShouldRetry(int? status) =>
        status is null or >= 500 or (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests;

    private async Task<int> AttemptAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.AttemptTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.CallbackUrl)
        {
            Content = new ByteArrayContent(webhookEvent.Body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("HeatPump-Event-Id", webhookEvent.EventId);
        request.Headers.Add("HeatPump-Signature", Signature(_secret, time.GetUtcNow(), webhookEvent.Body));

        using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered {Type} {EventId}, attempt {Attempt}: status {StatusCode} {Error}")]
    private partial void LogAttempt(string type, string eventId, int attempt, int? statusCode, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up on {EventId} after {Attempts} attempts")]
    private partial void LogGaveUp(string eventId, int attempts);
}
