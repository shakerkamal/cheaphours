namespace CheapHours.Core.Goals;

/// <summary>Why a plan version was made.</summary>
public enum PlanReason
{
    /// <summary>The goal was created.</summary>
    Created,

    /// <summary>The owner changed the goal's target or departure.</summary>
    GoalChanged,

    /// <summary>A day's prices were published.</summary>
    PricesPublished,

    /// <summary>The vehicle's reported charge drifted from what the plan expected.</summary>
    ChargeDrift,
}
