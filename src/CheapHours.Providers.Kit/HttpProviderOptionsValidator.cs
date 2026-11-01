using System.Globalization;
using CheapHours.Core.Ids;
using Microsoft.Extensions.Options;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Startup validation for <see cref="HttpProviderOptions"/> and <see cref="OAuthProviderOptions"/>. Errors name
/// the configuration key only and never contain a configured value, so no secret reaches a log.
/// </summary>
/// <typeparam name="TOptions">The provider's options type.</typeparam>
/// <param name="section">The provider's configuration section, used in the messages.</param>
internal sealed class HttpProviderOptionsValidator<TOptions>(string section) : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var http = options as HttpProviderOptions
            ?? throw new InvalidOperationException($"{typeof(TOptions).Name} does not derive from {nameof(HttpProviderOptions)}.");
        var problems = Problems(http).ToList();
        return problems.Count > 0 ? ValidateOptionsResult.Fail(problems) : ValidateOptionsResult.Success;
    }

    private IEnumerable<string> Problems(HttpProviderOptions options)
    {
        if (options.BaseAddress is not { IsAbsoluteUri: true })
        {
            yield return Required(nameof(options.BaseAddress), "an absolute URI");
        }

        if (options.PollInterval < HttpProviderOptions.MinimumPollInterval)
        {
            yield return Key(nameof(options.PollInterval)) + " must be at least 00:00:05.";
        }

        if (options.RateLimit.RequestsPerMinute < 1)
        {
            yield return Key($"{nameof(options.RateLimit)}:{nameof(RateLimitOptions.RequestsPerMinute)}") + " must be at least 1.";
        }

        foreach (var problem in PipelineProblems(options.Http))
        {
            yield return problem;
        }

        if (options is OAuthProviderOptions oauth)
        {
            foreach (var problem in OAuthProblems(oauth))
            {
                yield return problem;
            }
        }
    }

    private IEnumerable<string> PipelineProblems(HttpPipelineOptions http)
    {
        var half = TimeSpan.FromMilliseconds(500);
        if (http.TotalTimeout <= TimeSpan.Zero)
        {
            yield return Key("Http:TotalTimeout") + " must be positive.";
        }

        if (http.AttemptTimeout <= TimeSpan.Zero || http.AttemptTimeout > http.TotalTimeout)
        {
            yield return Key("Http:AttemptTimeout") + " must be positive and at most Http:TotalTimeout.";
        }

        if (http.MaxRetries < 0)
        {
            yield return Key("Http:MaxRetries") + " cannot be negative.";
        }

        if (http.RetryBaseDelay < TimeSpan.Zero)
        {
            yield return Key("Http:RetryBaseDelay") + " cannot be negative.";
        }

        if (http.BreakDuration < half)
        {
            yield return Key("Http:BreakDuration") + " must be at least 00:00:00.5.";
        }

        if (http.SamplingDuration < half)
        {
            yield return Key("Http:SamplingDuration") + " must be at least 00:00:00.5.";
        }

        if (http.FailureRatio is <= 0 or > 1)
        {
            yield return Key("Http:FailureRatio") + " must be above 0 and at most 1.";
        }

        if (http.MinimumThroughput < 2)
        {
            yield return Key("Http:MinimumThroughput") + " must be at least 2.";
        }
    }

    private IEnumerable<string> OAuthProblems(OAuthProviderOptions oauth)
    {
        if (oauth.TokenEndpoint is not { IsAbsoluteUri: true })
        {
            yield return Required(nameof(oauth.TokenEndpoint), "an absolute URI");
        }

        if (string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            yield return Required(nameof(oauth.ClientId));
        }

        if (string.IsNullOrWhiteSpace(oauth.ClientSecret))
        {
            yield return Required(nameof(oauth.ClientSecret));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < oauth.Accounts.Count; i++)
        {
            var account = oauth.Accounts[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"{nameof(oauth.Accounts)}:{i}:");
            if (!OwnerId.TryParse(account.Owner, out _))
            {
                yield return Required(prefix + nameof(account.Owner), "an owner id such as own_demo");
            }

            if (string.IsNullOrWhiteSpace(account.ExternalAccountId))
            {
                yield return Required(prefix + nameof(account.ExternalAccountId));
            }
            else if (!seen.Add(account.ExternalAccountId))
            {
                yield return Key(prefix + nameof(account.ExternalAccountId)) + " repeats another account.";
            }

            if (string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                yield return Required(prefix + nameof(account.RefreshToken));
            }
        }
    }

    private string Key(string key) => $"{section}:{key}";

    private string Required(string key, string? what = null) =>
        what is null ? $"{Key(key)} is required." : $"{Key(key)} is required ({what}).";
}
