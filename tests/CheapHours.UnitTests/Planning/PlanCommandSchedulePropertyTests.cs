using CheapHours.Core.Commands;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Planning;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CheapHours.UnitTests.Planning;

// The schedule's three rules over random plans, re-plans and times: every start is followed by a stop before
// the next start, no two commands share a key, and nothing is due after the window end (departure minus the
// margin).
public sealed class PlanCommandSchedulePropertyTests
{
    private const int Cases = 1_000;

    [Property(MaxTest = Cases, Arbitrary = [typeof(ScheduleArbitraries)])]
    public void Every_start_has_a_later_stop(ScheduleScenario scenario)
    {
        var schedule = scenario.Schedule();

        for (var i = 0; i < schedule.Count; i++)
        {
            if (schedule[i].Type != CommandType.StartCharging)
            {
                continue;
            }

            Assert.True(i + 1 < schedule.Count, $"start at {schedule[i].DueAt:O} has no stop");
            Assert.Equal(CommandType.StopCharging, schedule[i + 1].Type);
            Assert.True(schedule[i + 1].DueAt > schedule[i].DueAt, $"stop at {schedule[i + 1].DueAt:O} is not after its start");
        }
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(ScheduleArbitraries)])]
    public void No_two_commands_share_a_key(ScheduleScenario scenario)
    {
        var keys = scenario.Schedule().Select(c => c.IdempotencyKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(ScheduleArbitraries)])]
    public void Nothing_is_due_after_the_window_end(ScheduleScenario scenario)
    {
        Assert.All(scenario.Schedule(), c => Assert.True(c.DueAt <= scenario.WindowEnd, $"{c.Type} due {c.DueAt:O} after {scenario.WindowEnd:O}"));
    }

    [Property(MaxTest = Cases, Arbitrary = [typeof(ScheduleArbitraries)])]
    public void Schedule_is_in_due_order(ScheduleScenario scenario)
    {
        var due = scenario.Schedule().Select(c => c.DueAt).ToArray();

        Assert.Equal(due.Order(), due);
    }
}

/// <summary>
/// A generated case: a window of whole slots ending at <see cref="WindowEnd"/>, the time of the re-plan inside
/// it, the new version's slots (from the next whole slot on) and the previous version's slots (anywhere in the
/// window).
/// </summary>
public sealed record ScheduleScenario(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    DateTimeOffset Now,
    bool[] NewSlots,
    bool[] PreviousSlots,
    bool ChargerCharging)
{
    public IReadOnlyList<ScheduledCommand> Schedule()
    {
        var plan = Version(2, NewSlots, from: Ceiling(Now));
        var previous = Version(1, PreviousSlots, from: WindowStart);
        return PlanCommandSchedule.For(plan, previous, ChargerCharging, Now);
    }

    private static DateTimeOffset Ceiling(DateTimeOffset t) =>
        new(((t.Ticks + PlanSlot.Duration.Ticks - 1) / PlanSlot.Duration.Ticks) * PlanSlot.Duration.Ticks, TimeSpan.Zero);

    private Plan Version(int version, bool[] chosen, DateTimeOffset from)
    {
        var slots = new List<PlanSlot>();
        for (var (t, i) = (from, 0); t < WindowEnd && i < chosen.Length; t += PlanSlot.Duration, i++)
        {
            if (chosen[i])
            {
                slots.Add(new PlanSlot(t, 11_000));
            }
        }

        return new Plan(
            PlanId.Parse("plan_prop"),
            GoalId.Parse("goal_prop"),
            OwnerId.Parse("own_prop"),
            version,
            PlanStatus.Confirmed,
            slots,
            expectedCostEur: 0m,
            arrivalCostEur: 0m,
            targetMissed: false,
            pricesComplete: true,
            madeAtUtc: Now);
    }
}

public static class ScheduleArbitraries
{
    // Windows start around both 2026 daylight-saving changes and a normal day; slot maths never assumes 96.
    private static readonly DateTimeOffset[] Days =
    [
        new(2026, 3, 28, 22, 0, 0, TimeSpan.Zero),
        new(2026, 10, 24, 22, 0, 0, TimeSpan.Zero),
        new(2026, 11, 7, 22, 0, 0, TimeSpan.Zero),
    ];

    public static Arbitrary<ScheduleScenario> Scenario() =>
        (from day in Gen.Elements(Days)
         from windowSlots in Gen.Choose(1, 192)
         from nowSeconds in Gen.Choose(0, (windowSlots * 900) - 1)
         from newSlots in Gen.ArrayOf(ArbMap.Default.GeneratorFor<bool>(), windowSlots)
         from previousSlots in Gen.ArrayOf(ArbMap.Default.GeneratorFor<bool>(), windowSlots)
         from charging in ArbMap.Default.GeneratorFor<bool>()
         select new ScheduleScenario(
             day,
             day.AddMinutes(15 * windowSlots),
             day.AddSeconds(nowSeconds),
             newSlots,
             previousSlots,
             charging)).ToArbitrary();
}
