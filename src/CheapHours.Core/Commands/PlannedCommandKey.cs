using System.Globalization;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.Core.Commands;

/// <summary>
/// Builds the idempotency key of a command created from a plan slot. The same plan, version, slot and type
/// always give the same key, so re-running the planner for one version cannot create a second command; a new
/// plan version gives new keys.
/// </summary>
public static class PlannedCommandKey
{
    /// <summary>Builds the key, for example <c>plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging</c>.</summary>
    /// <param name="planId">The plan.</param>
    /// <param name="version">The plan version, 1 or more.</param>
    /// <param name="slot">The slot the command belongs to; only its start is used.</param>
    /// <param name="type">What the command does.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">A value is missing or out of range.</exception>
    public static string For(PlanId planId, int version, PlanSlot slot, CommandType type)
    {
        Guard.NotDefault(planId);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(slot);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown command type.");
        }

        // Unambiguous: ':' cannot occur in a plan id, the version is an integer, the time has a fixed width
        // and the type is an enum name. Slot starts are UTC, so the clock time printed is UTC.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{planId.Value}:v{version}:slot_{slot.StartUtc:yyyy-MM-dd'T'HH:mm'Z'}:{type}");
    }
}
