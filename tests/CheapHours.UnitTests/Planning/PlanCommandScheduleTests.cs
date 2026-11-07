using CheapHours.Core.Commands;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Planning;

namespace CheapHours.UnitTests.Planning;

public sealed class PlanCommandScheduleTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly PlanId PlanId = PlanId.Parse("plan_8812");

    [Fact]
    public void Two_runs_give_a_start_and_a_stop_each()
    {
        var plan = Version(1, At(1, 0), At(1, 15), At(3, 0));

        var schedule = PlanCommandSchedule.For(plan, previous: null, chargerCharging: false, now: At(0, 50));

        Assert.Equal(
            [
                (CommandType.StartCharging, At(1, 0), "plan_8812:v1:slot_2026-11-08T01:00Z:StartCharging"),
                (CommandType.StopCharging, At(1, 30), "plan_8812:v1:slot_2026-11-08T01:15Z:StopCharging"),
                (CommandType.StartCharging, At(3, 0), "plan_8812:v1:slot_2026-11-08T03:00Z:StartCharging"),
                (CommandType.StopCharging, At(3, 15), "plan_8812:v1:slot_2026-11-08T03:00Z:StopCharging"),
            ],
            schedule.Select(c => (c.Type, c.DueAt, c.IdempotencyKey)));
    }

    [Fact]
    public void Key_slot_is_the_first_slot_for_a_start_and_the_last_for_a_stop()
    {
        var plan = Version(1, At(1, 0), At(1, 15));

        var schedule = PlanCommandSchedule.For(plan, null, false, At(0, 50));

        Assert.Equal(At(1, 0), schedule[0].KeySlot.StartUtc);
        Assert.Equal(At(1, 15), schedule[1].KeySlot.StartUtc);
        Assert.Equal(PlannedCommandKey.For(PlanId, 1, schedule[1].KeySlot, CommandType.StopCharging), schedule[1].IdempotencyKey);
    }

    [Fact]
    public void Single_slot_gives_a_start_and_a_stop_keyed_by_the_same_slot()
    {
        var plan = Version(1, At(2, 45));

        var schedule = PlanCommandSchedule.For(plan, null, false, At(0, 50));

        Assert.Equal(
            [
                (CommandType.StartCharging, At(2, 45), "plan_8812:v1:slot_2026-11-08T02:45Z:StartCharging"),
                (CommandType.StopCharging, At(3, 0), "plan_8812:v1:slot_2026-11-08T02:45Z:StopCharging"),
            ],
            schedule.Select(c => (c.Type, c.DueAt, c.IdempotencyKey)));
    }

    [Fact]
    public void No_slots_give_no_commands()
    {
        Assert.Empty(PlanCommandSchedule.For(Version(1), null, false, At(0, 50)));
    }

    // charging-goals: "Charging continues".
    [Fact]
    public void Run_continuing_the_current_one_gets_no_start()
    {
        var previous = Version(1, Range(At(1, 0), At(2, 0)));
        var plan = Version(2, Range(At(1, 30), At(3, 0)));

        var schedule = PlanCommandSchedule.For(plan, previous, chargerCharging: true, now: At(1, 20));

        Assert.Equal(
            [(CommandType.StopCharging, At(3, 0), "plan_8812:v2:slot_2026-11-08T02:45Z:StopCharging")],
            schedule.Select(c => (c.Type, c.DueAt, c.IdempotencyKey)));
    }

    // charging-goals: "Charging moves later".
    [Fact]
    public void Charging_that_moves_later_stops_at_the_end_of_the_current_slot()
    {
        var previous = Version(1, Range(At(1, 0), At(2, 0)));
        var plan = Version(2, Range(At(4, 0), At(5, 0)));

        var schedule = PlanCommandSchedule.For(plan, previous, chargerCharging: true, now: At(1, 20));

        Assert.Equal(
            [
                (CommandType.StopCharging, At(1, 30), "plan_8812:v2:slot_2026-11-08T01:15Z:StopCharging"),
                (CommandType.StartCharging, At(4, 0), "plan_8812:v2:slot_2026-11-08T04:00Z:StartCharging"),
                (CommandType.StopCharging, At(5, 0), "plan_8812:v2:slot_2026-11-08T04:45Z:StopCharging"),
            ],
            schedule.Select(c => (c.Type, c.DueAt, c.IdempotencyKey)));
    }

    [Fact]
    public void Re_plan_with_nothing_left_to_charge_still_stops_the_current_run()
    {
        var previous = Version(1, Range(At(1, 0), At(2, 0)));

        var schedule = PlanCommandSchedule.For(Version(2), previous, chargerCharging: true, now: At(1, 20));

        var stop = Assert.Single(schedule);
        Assert.Equal((CommandType.StopCharging, At(1, 30)), (stop.Type, stop.DueAt));
    }

    [Fact]
    public void Charger_not_charging_yet_gets_a_normal_schedule()
    {
        var previous = Version(1, Range(At(1, 0), At(2, 0)));
        var plan = Version(2, Range(At(1, 30), At(3, 0)));

        var schedule = PlanCommandSchedule.For(plan, previous, chargerCharging: false, now: At(1, 20));

        Assert.Equal([CommandType.StartCharging, CommandType.StopCharging], schedule.Select(c => c.Type));
        Assert.Equal(At(1, 30), schedule[0].DueAt);
    }

    [Fact]
    public void Previous_version_not_charging_in_the_current_slot_gets_a_normal_schedule()
    {
        // The previous run ended at 01:00; its stop may still be unconfirmed, but the current slot is free.
        var previous = Version(1, Range(At(0, 0), At(1, 0)));
        var plan = Version(2, Range(At(1, 30), At(2, 0)));

        var schedule = PlanCommandSchedule.For(plan, previous, chargerCharging: true, now: At(1, 20));

        Assert.Equal([CommandType.StartCharging, CommandType.StopCharging], schedule.Select(c => c.Type));
    }

    [Fact]
    public void Re_plan_on_a_slot_boundary_continues_a_run_that_includes_the_current_slot()
    {
        var previous = Version(1, Range(At(1, 0), At(2, 0)));
        var plan = Version(2, Range(At(1, 30), At(2, 30)));

        var schedule = PlanCommandSchedule.For(plan, previous, chargerCharging: true, now: At(1, 30));

        var stop = Assert.Single(schedule);
        Assert.Equal((CommandType.StopCharging, At(2, 30)), (stop.Type, stop.DueAt));
    }

    [Fact]
    public void Now_that_is_not_utc_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PlanCommandSchedule.For(Version(1), null, false, new DateTimeOffset(2026, 11, 8, 2, 0, 0, TimeSpan.FromHours(1))));
    }

    private static DateTimeOffset At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    private static DateTimeOffset[] Range(DateTimeOffset from, DateTimeOffset to)
    {
        var starts = new List<DateTimeOffset>();
        for (var t = from; t < to; t += PlanSlot.Duration)
        {
            starts.Add(t);
        }

        return [.. starts];
    }

    private static Plan Version(int version, params DateTimeOffset[] starts) =>
        new(
            PlanId,
            GoalId.Parse("goal_1"),
            OwnerId.Parse("own_demo"),
            version,
            PlanStatus.Confirmed,
            starts.Select(s => new PlanSlot(s, 11_000)).ToArray(),
            expectedCostEur: 1m,
            arrivalCostEur: 2m,
            targetMissed: false,
            pricesComplete: true,
            madeAtUtc: Day);
}
