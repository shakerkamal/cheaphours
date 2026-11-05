using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace CheapHours.Host.Prices;

/// <summary>Registers the price settings and the price source.</summary>
internal static class PricesSetup
{
    /// <summary>Registers <see cref="PricesOptions"/>, validated at start. The API and the worker both need them.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPriceOptions(this IServiceCollection services)
    {
        services.AddOptions<PricesOptions>().BindConfiguration(PricesOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PricesOptions>, PricesOptionsValidator>());
        return services;
    }

    /// <summary>
    /// Registers the Energy-Charts client as the <see cref="IPriceSource"/>, with its own named client and pipeline:
    /// total timeout, jittered retry for 5xx, 408, timeouts and connection failures (never 429), attempt timeout.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPriceSource(this IServiceCollection services)
    {
        services.AddPriceOptions();
        services.AddHttpClient(EnergyChartsClient.ClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan; // the pipeline's timeouts govern
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CheapHours/1.0");
            })
            .AddResilienceHandler("prices", (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<PricesOptions>>().Value;
                pipeline.AddTimeout(options.TotalTimeout);
                if (options.MaxRetries > 0)
                {
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = options.MaxRetries,
                        Delay = options.RetryBaseDelay,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        ShouldRetryAfterHeader = false,
                        ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                    });
                }

                pipeline.AddTimeout(options.AttemptTimeout);
            });
        services.AddSingleton<IPriceSource, EnergyChartsClient>();
        return services;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result is { StatusCode: HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError };
}
