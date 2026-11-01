using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace CheapHours.Providers.Kit;

/// <summary>
/// The vendor resilience pipeline of one provider (design §1), outermost first: total timeout, jittered
/// retry, circuit breaker, attempt timeout. The retry and the breaker handle 5xx, 408, attempt timeouts and
/// connection failures; a 429 or other 4xx is never retried here (a 429's <c>Retry-After</c> goes back to the
/// caller, which pauses the account instead of sleeping inside a call).
/// </summary>
internal static class VendorPipeline
{
    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, HttpPipelineOptions options)
    {
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

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            BreakDuration = options.BreakDuration,
            FailureRatio = options.FailureRatio,
            MinimumThroughput = options.MinimumThroughput,
            SamplingDuration = options.SamplingDuration,
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
        });

        pipeline.AddTimeout(options.AttemptTimeout);
    }

    /// <summary>Whether an attempt's outcome counts as transient for the retry and the breaker.</summary>
    /// <param name="outcome">The attempt's outcome.</param>
    /// <returns><see langword="true"/> for 5xx, 408, attempt timeouts and connection failures.</returns>
    public static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result is { StatusCode: HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError };
}
