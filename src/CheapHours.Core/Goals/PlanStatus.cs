namespace CheapHours.Core.Goals;

/// <summary>Where a plan version is in its life.</summary>
public enum PlanStatus
{
    /// <summary>Proposed; changes nothing until confirmed, and expires.</summary>
    Draft,

    /// <summary>Confirmed by the owner; its slots become commands.</summary>
    Confirmed,

    /// <summary>Replaced by a newer version (new prices, goal change, charge drift).</summary>
    Superseded,

    /// <summary>A draft that was not confirmed before its expiry.</summary>
    Expired,
}
