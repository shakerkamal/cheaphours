using CheapHours.Planning;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Goals;

/// <summary>Runs <see cref="PlannerOptionsValidator"/> at startup; problems name the key, never the value.</summary>
internal sealed class PlannerOptionsHostValidator : IValidateOptions<PlannerOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, PlannerOptions options)
    {
        var problems = PlannerOptionsValidator.Validate(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
