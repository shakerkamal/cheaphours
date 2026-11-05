using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CheapHours.Host.Prices;

/// <summary>
/// Reads day-ahead prices from the Energy-Charts <c>/price</c> endpoint, one Berlin day per request (design §2).
/// Retries for 5xx, 408, timeouts and connection failures live in the named client's pipeline; a 429 is never
/// retried. Every response is checked by <see cref="PriceResponseValidator"/> before it is returned. Bodies are
/// never logged.
/// </summary>
internal sealed partial class EnergyChartsClient(IHttpClientFactory clients, IOptions<PricesOptions> options, ILogger<EnergyChartsClient> logger) : IPriceSource
{
    /// <summary>The named <see cref="HttpClient"/>.</summary>
    public const string ClientName = "energy-charts";

    /// <inheritdoc/>
    public async Task<PriceFetch> FetchAsync(string zone, DateOnly day, CancellationToken cancellationToken)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var uri = new Uri(options.Value.BaseUrl, $"price?bzn={Uri.EscapeDataString(zone)}&start={date}&end={date}");
        var client = clients.CreateClient(ClientName);

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ReadAsync(zone, day, response, cancellationToken).ConfigureAwait(false);
                case HttpStatusCode.NotFound:
                    return new PriceFetch.NotPublished();
                case HttpStatusCode.TooManyRequests:
                    return new PriceFetch.RateLimited();
                default:
                    return new PriceFetch.Failed(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new PriceFetch.Failed(ex.GetType().Name);
        }
    }

    private async Task<PriceFetch> ReadAsync(string zone, DateOnly day, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        EnergyChartsPriceResponse? body;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                body = await JsonSerializer.DeserializeAsync<EnergyChartsPriceResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            LogUnreadable(zone, day);
            return new PriceFetch.Failed(nameof(JsonException));
        }

        if (body is null)
        {
            LogUnreadable(zone, day);
            return new PriceFetch.Failed(nameof(JsonException));
        }

        switch (PriceResponseValidator.Validate(zone, day, body))
        {
            case PriceValidation.Accepted accepted:
                return new PriceFetch.Published(accepted.Day, body.Deprecated);
            case PriceValidation.Rejected rejected:
                LogRejected(zone, day, rejected.Rule);
                return new PriceFetch.Rejected(rejected.Rule);
            default:
                throw new InvalidOperationException("Unknown validation outcome.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price response for {Zone} on {Day} rejected: rule {Rule} broken; nothing stored")]
    private partial void LogRejected(string zone, DateOnly day, PriceRule rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price response for {Zone} on {Day} is not readable JSON; nothing stored")]
    private partial void LogUnreadable(string zone, DateOnly day);
}
