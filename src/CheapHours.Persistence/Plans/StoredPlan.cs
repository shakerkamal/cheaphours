using CheapHours.Core.Goals;

namespace CheapHours.Persistence.Plans;

/// <summary>
/// One stored plan version with what it was made from: the reason, the vehicle's charge it started from, the
/// target and the energy it planned for, and when the owner confirmed it (kept through re-plans).
/// </summary>
/// <param name="Plan">The plan version.</param>
/// <param name="Reason">Why this version was made.</param>
/// <param name="StartSocPercent">The vehicle's charge the plan started from.</param>
/// <param name="TargetSocPercent">The target charge it planned for.</param>
/// <param name="EnergyNeededWh">The grid energy it planned for.</param>
/// <param name="ConfirmedAt">When the owner confirmed it, or <see langword="null"/>.</param>
/// <param name="ProposedTargetSocPercent">The target a change draft proposes; <see langword="null"/> on other versions.</param>
/// <param name="ProposedDepartureAt">The departure a change draft proposes (UTC); <see langword="null"/> on other versions.</param>
public sealed record StoredPlan(
    Plan Plan,
    PlanReason Reason,
    decimal StartSocPercent,
    int TargetSocPercent,
    long EnergyNeededWh,
    DateTimeOffset? ConfirmedAt = null,
    int? ProposedTargetSocPercent = null,
    DateTimeOffset? ProposedDepartureAt = null)
{
    /// <summary>Gets whether this version proposes a goal change that confirming it applies.</summary>
    public bool IsChangeDraft => ProposedTargetSocPercent is not null || ProposedDepartureAt is not null;

    /// <summary>Gets the document id of this version: <c>{planId}:v{version}</c>.</summary>
    public string VersionId => VersionIdOf(Plan.Id.Value, Plan.Version);

    internal static string VersionIdOf(string planId, int version) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{planId}:v{version}");
}
