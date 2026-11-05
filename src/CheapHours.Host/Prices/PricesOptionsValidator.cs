using Microsoft.Extensions.Options;

namespace CheapHours.Host.Prices;

/// <summary>Checks <see cref="PricesOptions"/> at startup, naming every broken key and never a value.</summary>
internal sealed class PricesOptionsValidator : IValidateOptions<PricesOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, PricesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Zone))
        {
            problems.Add("Prices: Zone is required.");
        }

        if (options.BaseUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add("Prices: BaseUrl must be an absolute http(s) URL.");
        }

        if (options.FirstAttemptAt >= options.GiveUpAt)
        {
            problems.Add("Prices: FirstAttemptAt must be before GiveUpAt.");
        }

        if (options.RetryInterval < TimeSpan.FromMinutes(1) || options.RetryInterval > TimeSpan.FromHours(6))
        {
            problems.Add("Prices: RetryInterval must be between 1 minute and 6 hours.");
        }

        if (options.PauseBetweenRequests < TimeSpan.Zero)
        {
            problems.Add("Prices: PauseBetweenRequests must not be negative.");
        }

        if (options.AttemptTimeout <= TimeSpan.Zero || options.TotalTimeout < options.AttemptTimeout)
        {
            problems.Add("Prices: AttemptTimeout must be positive and no longer than TotalTimeout.");
        }

        if (options.MaxRetries is < 0 or > 10 || options.RetryBaseDelay <= TimeSpan.Zero)
        {
            problems.Add("Prices: MaxRetries must be between 0 and 10, and RetryBaseDelay positive.");
        }

        if (options.LeaseDuration < options.TotalTimeout * 4)
        {
            problems.Add("Prices: LeaseDuration must be at least four times TotalTimeout, so a run fits in half of it.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
