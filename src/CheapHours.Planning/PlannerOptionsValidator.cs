namespace CheapHours.Planning;

/// <summary>
/// Checks <see cref="PlannerOptions"/> against their ranges. Problems name the key, never the value. The host
/// runs this at startup; it is a plain function so that this module needs no options package.
/// </summary>
public static class PlannerOptionsValidator
{
    /// <summary>Returns every problem with <paramref name="options"/>, or none.</summary>
    /// <param name="options">The options to check.</param>
    /// <returns>One message per broken rule.</returns>
    public static IReadOnlyList<string> Validate(PlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();

        Check(problems, options.SafetyMargin >= TimeSpan.Zero && options.SafetyMargin <= TimeSpan.FromHours(4),
            "SafetyMargin must be between 0 and 4 hours.");
        Check(problems, options.ChargingEfficiency is >= 0.5m and <= 1.0m,
            "ChargingEfficiency must be between 0.5 and 1.0.");
        Check(problems, options.DraftExpiry >= TimeSpan.FromMinutes(1) && options.DraftExpiry <= TimeSpan.FromMinutes(60),
            "DraftExpiry must be between 1 and 60 minutes.");
        Check(problems, options.MaxHorizon >= TimeSpan.FromHours(1) && options.MaxHorizon <= TimeSpan.FromDays(7),
            "MaxHorizon must be between 1 hour and 7 days.");
        Check(problems, options.DriftThresholdPercent is >= 1m and <= 50m,
            "DriftThresholdPercent must be between 1 and 50.");
        Check(problems, options.DriftCheckInterval >= TimeSpan.FromSeconds(30) && options.DriftCheckInterval <= TimeSpan.FromHours(1),
            "DriftCheckInterval must be between 30 seconds and 1 hour.");

        return problems;
    }

    private static void Check(List<string> problems, bool ok, string message)
    {
        if (!ok)
        {
            problems.Add($"{PlannerOptions.SectionName}: {message}");
        }
    }
}
