using System.Globalization;
using CheapHours.Core.Ids;
using Microsoft.Extensions.Options;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Startup validation for <see cref="WebhookProviderOptions"/>. Errors name the configuration key only and never
/// contain a configured value, so the signing secret never reaches a log.
/// </summary>
/// <typeparam name="TOptions">The provider's options type.</typeparam>
/// <param name="section">The provider's configuration section, used in the messages.</param>
internal sealed class WebhookProviderOptionsValidator<TOptions>(string section) : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var webhook = options as WebhookProviderOptions
            ?? throw new InvalidOperationException($"{typeof(TOptions).Name} does not derive from {nameof(WebhookProviderOptions)}.");
        var problems = Problems(webhook).ToList();
        return problems.Count > 0 ? ValidateOptionsResult.Fail(problems) : ValidateOptionsResult.Success;
    }

    private IEnumerable<string> Problems(WebhookProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningSecret))
        {
            yield return Key(nameof(options.SigningSecret)) + " is required.";
        }
        else if (options.SigningSecret.Length < WebhookProviderOptions.MinimumSecretLength)
        {
            yield return Key(nameof(options.SigningSecret)) + string.Create(CultureInfo.InvariantCulture, $" must be at least {WebhookProviderOptions.MinimumSecretLength} characters.");
        }

        if (options.SignatureTolerance < WebhookProviderOptions.MinimumTolerance || options.SignatureTolerance > WebhookProviderOptions.MaximumTolerance)
        {
            yield return Key(nameof(options.SignatureTolerance)) + " must be between 00:00:01 and 00:15:00.";
        }

        if (options.ExpectedReportInterval < WebhookProviderOptions.MinimumReportInterval)
        {
            yield return Key(nameof(options.ExpectedReportInterval)) + " must be at least 00:00:05.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Accounts.Count; i++)
        {
            var account = options.Accounts[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"{nameof(options.Accounts)}:{i}:");
            if (!OwnerId.TryParse(account.Owner, out _))
            {
                yield return Key(prefix + nameof(account.Owner)) + " is required (an owner id such as own_demo).";
            }

            if (string.IsNullOrWhiteSpace(account.ExternalAccountId))
            {
                yield return Key(prefix + nameof(account.ExternalAccountId)) + " is required.";
            }
            else if (!seen.Add(account.ExternalAccountId))
            {
                yield return Key(prefix + nameof(account.ExternalAccountId)) + " repeats another account.";
            }
        }
    }

    private string Key(string key) => $"{section}:{key}";
}
