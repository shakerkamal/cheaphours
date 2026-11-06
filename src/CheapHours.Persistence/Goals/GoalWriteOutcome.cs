namespace CheapHours.Persistence.Goals;

/// <summary>What a goal write did. Nothing is written unless the outcome is <see cref="Stored"/>.</summary>
public enum GoalWriteOutcome
{
    /// <summary>The goal and its plan version were stored.</summary>
    Stored,

    /// <summary>The vehicle or the charger already belongs to another open goal.</summary>
    DeviceBusy,

    /// <summary>Another writer stored this plan version first.</summary>
    VersionTaken,
}
