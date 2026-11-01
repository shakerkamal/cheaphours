using System.Net;
using System.Net.Http.Headers;

namespace CheapHours.Providers.Kit;

/// <summary>
/// The one way a provider calls its vendor (design §1). Each call:
/// <list type="number">
/// <item>takes a token from the account's rate limit (waiting at most 2 s);</item>
/// <item>for OAuth providers, attaches the account's access token as a bearer token;</item>
/// <item>goes through the provider's own named client and resilience pipeline;</item>
/// <item>for OAuth providers, after a 401 refreshes the token once and sends once more (a second 401 is reconnect-needed);</item>
/// <item>is classified and measured the same way for every provider.</item>
/// </list>
/// Vendor failures come back as <see cref="VendorResponse.Failed"/>, never as exceptions.
/// </summary>
/// <typeparam name="TProvider">The provider this client belongs to; it takes the client in its constructor.</typeparam>
public sealed class ProviderHttpClient<TProvider>
    where TProvider : class, IDeviceProvider
{
    private readonly IHttpClientFactory _clients;
    private readonly HttpProviderOptions _options;
    private readonly AccountRateLimiter _limiter;
    private readonly ProviderMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly OAuthTokenStore? _tokens;

    internal ProviderHttpClient(
        IHttpClientFactory clients,
        HttpProviderOptions options,
        AccountRateLimiter limiter,
        ProviderMetrics metrics,
        TimeProvider time,
        OAuthTokenStore? tokens)
    {
        _clients = clients;
        _options = options;
        _limiter = limiter;
        _metrics = metrics;
        _time = time;
        _tokens = tokens;
    }

    /// <summary>Gets the name of the provider's named <see cref="HttpClient"/>: the provider type's full name.</summary>
    public string ClientName { get; } = typeof(TProvider).FullName!;

    /// <summary>Sends one request for an account.</summary>
    /// <param name="account">The account the call is made for; its rate limit and tokens apply.</param>
    /// <param name="request">
    /// Builds the request, relative to the vendor's base address. It is called again after a token refresh,
    /// so it must build a new message each time. The kit sets the <c>Authorization</c> header.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The response or the classified failure. Dispose it.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public async Task<VendorResponse> SendAsync(ProviderAccount account, Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);

        var started = _time.GetTimestamp();
        var outcome = await SendCoreAsync(account, request, cancellationToken).ConfigureAwait(false);
        _metrics.Record(account.ProviderId, (outcome as VendorResponse.Failed)?.Failure, _time.GetElapsedTime(started));
        return outcome;
    }

    private async Task<VendorResponse> SendCoreAsync(ProviderAccount account, Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        if (await _limiter.AcquireAsync(account.ProviderId, account.ExternalAccountId, _options.RateLimit.RequestsPerMinute, cancellationToken).ConfigureAwait(false) is { } wait)
        {
            return new VendorResponse.Failed(new VendorFailure(VendorFailureKind.Transient, VendorFailure.RateLimited, wait));
        }

        var lease = default(TokenLease);
        if (_tokens is not null)
        {
            lease = await _tokens.GetAsync(account, cancellationToken).ConfigureAwait(false);
            if (lease.Failure is { } failure)
            {
                return new VendorResponse.Failed(failure);
            }
        }

        try
        {
            var response = await SendOnceAsync(request, lease.AccessToken, cancellationToken).ConfigureAwait(false);
            if (_tokens is not null && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var renewed = await _tokens.RefreshAfterRejectionAsync(account, lease.Version, cancellationToken).ConfigureAwait(false);
                if (renewed.Failure is { } refreshFailure)
                {
                    return new VendorResponse.Failed(refreshFailure);
                }

                response = await SendOnceAsync(request, renewed.AccessToken, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    return new VendorResponse.Failed(await _tokens.MarkReconnectNeededAsync(account, "vendor refused a freshly refreshed token", cancellationToken).ConfigureAwait(false));
                }
            }

            return await ClassifyAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (VendorFailureClassifier.Classify(ex, cancellationToken) is { } failure)
        {
            return new VendorResponse.Failed(failure.Reason == VendorFailure.CircuitOpen
                ? new VendorFailure(failure.Kind, failure.Reason, _options.Http.BreakDuration)
                : failure);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> request, string? accessToken, CancellationToken cancellationToken)
    {
        using var message = request();
        if (accessToken is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _clients.CreateClient(ClientName).SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VendorResponse> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        VendorFailure? failure;
        try
        {
            failure = await VendorFailureClassifier.ClassifyAsync(response, _time, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (failure is null)
        {
            return new VendorResponse.Succeeded(response);
        }

        response.Dispose();
        return new VendorResponse.Failed(failure);
    }
}
