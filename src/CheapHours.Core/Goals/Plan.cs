using CheapHours.Core.Ids;

namespace CheapHours.Core.Goals;

/// <summary>
/// One version of the plan for a goal: the chosen slots and two costs. The difference between the cost of
/// charging on arrival and the plan's cost is the savings figure the user sees.
/// </summary>
public sealed record Plan
{
    /// <summary>Creates a plan version.</summary>
    /// <param name="id">The plan's id.</param>
    /// <param name="goalId">The goal it serves.</param>
    /// <param name="ownerId">The owner (D3).</param>
    /// <param name="version">The version, 1 or more; re-planning creates a new version.</param>
    /// <param name="status">The plan's status.</param>
    /// <param name="slots">The chosen 15-minute slots, in ascending order without overlaps.</param>
    /// <param name="expectedCostEur">The expected energy cost of the plan in EUR (exchange price only; may be negative).</param>
    /// <param name="arrivalCostEur">The cost of charging at full power from plug-in instead, in EUR.</param>
    /// <param name="expiresAtUtc">When a draft expires (UTC); required for drafts, ignored otherwise.</param>
    /// <exception cref="ArgumentException">A value is missing or breaks a rule above.</exception>
    public Plan(
        PlanId id,
        GoalId goalId,
        OwnerId ownerId,
        int version,
        PlanStatus status,
        IReadOnlyList<PlanSlot> slots,
        decimal expectedCostEur,
        decimal arrivalCostEur,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown plan status.");
        }

        if (status == PlanStatus.Draft && expiresAtUtc is null)
        {
            throw new ArgumentException("A draft plan needs an expiry time.", nameof(expiresAtUtc));
        }

        for (var i = 1; i < slots.Count; i++)
        {
            if (slots[i].StartUtc < slots[i - 1].EndUtc)
            {
                throw new ArgumentException($"Slots must be in ascending order without overlaps; slot {i} starts at {slots[i].StartUtc:O}.", nameof(slots));
            }
        }

        Id = Guard.NotDefault(id);
        GoalId = Guard.NotDefault(goalId);
        OwnerId = Guard.NotDefault(ownerId);
        Version = version;
        Status = status;
        Slots = [.. slots];
        ExpectedCostEur = expectedCostEur;
        ArrivalCostEur = arrivalCostEur;
        ExpiresAtUtc = status == PlanStatus.Draft ? Guard.Utc(expiresAtUtc) : null;
    }

    /// <summary>Gets the plan's id.</summary>
    public PlanId Id { get; }

    /// <summary>Gets the goal it serves.</summary>
    public GoalId GoalId { get; }

    /// <summary>Gets the owner.</summary>
    public OwnerId OwnerId { get; }

    /// <summary>Gets the version.</summary>
    public int Version { get; }

    /// <summary>Gets the status.</summary>
    public PlanStatus Status { get; }

    /// <summary>Gets the chosen slots, in ascending order.</summary>
    public IReadOnlyList<PlanSlot> Slots { get; }

    /// <summary>Gets the expected cost of the plan in EUR.</summary>
    public decimal ExpectedCostEur { get; }

    /// <summary>Gets the cost of charging on arrival in EUR.</summary>
    public decimal ArrivalCostEur { get; }

    /// <summary>Gets the savings against charging on arrival, in EUR.</summary>
    public decimal SavingsEur => ArrivalCostEur - ExpectedCostEur;

    /// <summary>Gets when the draft expires (UTC), or <see langword="null"/> when the plan is not a draft.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
}
