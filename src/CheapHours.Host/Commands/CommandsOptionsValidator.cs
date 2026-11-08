using Microsoft.Extensions.Options;

namespace CheapHours.Host.Commands;

/// <summary>Checks <see cref="CommandsOptions"/> at startup, naming every broken key and never a value.</summary>
internal sealed class CommandsOptionsValidator : IValidateOptions<CommandsOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, CommandsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();

        if (options.MaxAttempts is < 1 or > 20)
        {
            problems.Add("Commands: MaxAttempts must be between 1 and 20.");
        }

        if (options.BaseDelay <= TimeSpan.Zero || options.MaxDelay < options.BaseDelay)
        {
            problems.Add("Commands: BaseDelay must be positive and no longer than MaxDelay.");
        }

        if (options.ConfirmationTimeout <= TimeSpan.Zero)
        {
            problems.Add("Commands: ConfirmationTimeout must be positive.");
        }

        if (options.DispatchTimeout <= TimeSpan.Zero)
        {
            problems.Add("Commands: DispatchTimeout must be positive.");
        }

        if (options.MaxReannouncements is < 1 or > 20)
        {
            problems.Add("Commands: MaxReannouncements must be between 1 and 20.");
        }

        if (options.ClaimDuration <= TimeSpan.Zero)
        {
            problems.Add("Commands: ClaimDuration must be positive.");
        }

        if (options.MonitorInterval <= TimeSpan.Zero || options.RelayInterval <= TimeSpan.Zero)
        {
            problems.Add("Commands: MonitorInterval and RelayInterval must be positive.");
        }

        if (options.RelayBatchSize is < 1 or > 1000)
        {
            problems.Add("Commands: RelayBatchSize must be between 1 and 1000.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
