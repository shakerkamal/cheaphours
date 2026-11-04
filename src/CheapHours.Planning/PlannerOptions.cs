namespace CheapHours.Planning;

/// <summary>
/// Planner settings (configuration section <c>Planner</c>). The planner itself uses the safety margin and the
/// charging efficiency; the other values belong to goals and re-planning and sit here so one section holds
/// every planning rule.
/// </summary>
public sealed class PlannerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Planner";

    /// <summary>Gets or sets how long before departure charging must end. Default 30 minutes; 0 to 4 hours.</summary>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets or sets the share of grid energy that reaches the battery. Default 0.90; 0.5 to 1.0.</summary>
    public decimal ChargingEfficiency { get; set; } = 0.90m;

    /// <summary>Gets or sets how long a draft stays open for confirmation. Default 10 minutes; 1 to 60 minutes.</summary>
    public TimeSpan DraftExpiry { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets how far ahead a departure may be. Default 48 hours; 1 hour to 7 days.</summary>
    public TimeSpan MaxHorizon { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Gets or sets the charge difference, in percentage points, that re-plans a confirmed plan. Default 5; 1 to 50.</summary>
    public decimal DriftThresholdPercent { get; set; } = 5m;

    /// <summary>Gets or sets how often the drift check runs. Default 5 minutes; 30 seconds to 1 hour.</summary>
    public TimeSpan DriftCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
}
