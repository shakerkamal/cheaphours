using CheapHours.Core;
using CheapHours.Core.Commands;
using CheapHours.Core.Goals;

namespace CheapHours.Planning;

/// <summary>
/// Turns a confirmed plan version into the commands that carry it out (D5 keys, ADR-006). Each run of
/// consecutive slots becomes one <see cref="CommandType.StartCharging"/> at its first slot and one
/// <see cref="CommandType.StopCharging"/> at the end of its last, so two adjacent cheap slots never get a stop
/// and a start between them. When a re-plan replaces a version that is charging right now, charging carries on
/// without a gap: a run continuing the current one gets no start, and if nothing continues it, a stop is due
/// at the end of the current slot.
/// </summary>
public static class PlanCommandSchedule
{
    /// <summary>Builds the schedule.</summary>
    /// <param name="plan">The version being confirmed.</param>
    /// <param name="previous">The running version it replaces, if any.</param>
    /// <param name="chargerCharging">Whether the previous version's current run is under way: its start was handed to the device and its stop is not confirmed.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The commands in due order.</returns>
    /// <exception cref="ArgumentException"><paramref name="now"/> is not UTC.</exception>
    public static IReadOnlyList<ScheduledCommand> For(Plan plan, Plan? previous, bool chargerCharging, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // The slot under way; only its start and end matter (keys never include the power). Floor checks UTC.
        var current = new PlanSlot(SlotGrid.Floor(now), powerW: 1);
        var chargingNow = chargerCharging
            && previous is not null
            && previous.Slots.Any(s => s.StartUtc == current.StartUtc);

        var commands = new List<ScheduledCommand>();
        var continued = false;
        foreach (var run in Runs(plan.Slots))
        {
            var first = run[0];
            var last = run[^1];

            // A run that contains the current slot or starts right after it carries on the charging under way.
            var continues = chargingNow && first.StartUtc <= current.EndUtc && last.EndUtc > current.StartUtc;
            continued |= continues;
            if (!continues)
            {
                commands.Add(Command(plan, CommandType.StartCharging, first.StartUtc, first));
            }

            commands.Add(Command(plan, CommandType.StopCharging, last.EndUtc, last));
        }

        if (chargingNow && !continued)
        {
            commands.Add(Command(plan, CommandType.StopCharging, current.EndUtc, current));
        }

        return [.. commands.OrderBy(c => c.DueAt)];
    }

    private static ScheduledCommand Command(Plan plan, CommandType type, DateTimeOffset dueAt, PlanSlot keySlot) =>
        new(type, dueAt, keySlot, PlannedCommandKey.For(plan.Id, plan.Version, keySlot, type));

    private static IEnumerable<PlanSlot[]> Runs(IReadOnlyList<PlanSlot> slots)
    {
        var run = new List<PlanSlot>();
        foreach (var slot in slots)
        {
            if (run.Count > 0 && run[^1].EndUtc != slot.StartUtc)
            {
                yield return [.. run];
                run.Clear();
            }

            run.Add(slot);
        }

        if (run.Count > 0)
        {
            yield return [.. run];
        }
    }
}
