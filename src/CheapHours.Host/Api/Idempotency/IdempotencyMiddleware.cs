using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using CheapHours.Persistence.Idempotency;

namespace CheapHours.Host.Api.Idempotency;

/// <summary>
/// Makes requests to endpoints marked <see cref="IdempotencyEndpointExtensions.Idempotent{TBuilder}"/> safe to repeat
/// (design §10). It runs after authorization, so the owner is known, and does nothing without the header. A key
/// is 1–255 visible ASCII characters and belongs to the key's owner for 24 hours. The first request runs with its
/// response captured: below 500 it is kept and replayed for a repeat with the same method, route and body; 500
/// and above (or an exception) forget the key so the client can retry. A repeat with another body gets
/// <c>idempotency-key-reused</c>; a repeat while the first runs gets <c>conflict</c>, unless the first has run
/// for more than a minute, when it counts as abandoned.
/// </summary>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="store">The idempotency records.</param>
/// <param name="time">The clock.</param>
internal sealed class IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store, TimeProvider time)
{
    /// <summary>How long a key is remembered.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    /// <summary>How long a first request may run before a repeat takes it over.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(1);

    private const int MaxKeyLength = 255;

    /// <summary>Handles one request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentMetadata>() is null
            || !context.Request.Headers.TryGetValue(V1Endpoints.IdempotencyKeyHeader, out var values)
            || context.User.FindFirstValue(ApiScopes.OwnerClaimType) is not { } owner)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (values.Count != 1 || values[0] is not { } key || !IsValidKey(key))
        {
            await ErrorCatalogue.ValidationFailed(new Dictionary<string, string[]>
            {
                [V1Endpoints.IdempotencyKeyHeader] = [$"Must be one value of 1 to {MaxKeyLength} visible ASCII characters."],
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var id = owner + "|" + key;
        var fingerprint = await FingerprintAsync(context).ConfigureAwait(false);
        switch (await store.StartAsync(id, fingerprint, time.GetUtcNow(), KeepFor, AbandonAfter, context.RequestAborted).ConfigureAwait(false))
        {
            case IdempotencyClaim.Replay replay:
                await ReplayAsync(context, replay.Response).ConfigureAwait(false);
                return;
            case IdempotencyClaim.Reused:
                await ErrorCatalogue.IdempotencyKeyReused
                    .ToResult("This Idempotency-Key was used with a different request. Use a new key for a new request.")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            case IdempotencyClaim.InFlight:
                await ErrorCatalogue.Conflict
                    .ToResult("A request with this Idempotency-Key is still being handled. Repeat it once that one has finished.")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
        }

        await RunAndKeepAsync(context, id).ConfigureAwait(false);
    }

    // Visible ASCII only: no spaces, control characters or anything outside 0x21–0x7E.
    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => c is >= '!' and <= '~');

    // SHA-256 over the method, the route pattern, the route values and the body bytes.
    private static async Task<string> FingerprintAsync(HttpContext context)
    {
        var request = context.Request;
        request.EnableBuffering();
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
        request.Body.Position = 0;

        var route = context.GetEndpoint() is RouteEndpoint endpoint ? endpoint.RoutePattern.RawText : request.Path.Value;
        var values = string.Join('&', request.RouteValues
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => v.Key + "=" + Convert.ToString(v.Value, CultureInfo.InvariantCulture)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{route}\n{values}\n"));
        hash.AppendData(body.GetBuffer(), 0, (int)body.Length);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task ReplayAsync(HttpContext context, StoredResponse response)
    {
        context.Response.StatusCode = response.Status;
        if (response.ContentType is { } contentType)
        {
            context.Response.ContentType = contentType;
        }

        if (response.Location is { } location)
        {
            context.Response.Headers.Location = location;
        }

        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task RunAndKeepAsync(HttpContext context, string id)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = original;
            await store.ForgetAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        context.Response.Body = original;
        var status = context.Response.StatusCode;
        if (status < StatusCodes.Status500InternalServerError)
        {
            var location = context.Response.Headers.Location.ToString();
            await store.CompleteAsync(
                id,
                new StoredResponse(status, context.Response.ContentType, string.IsNullOrEmpty(location) ? null : location, buffer.ToArray()),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await store.ForgetAsync(id, CancellationToken.None).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
    }
}
