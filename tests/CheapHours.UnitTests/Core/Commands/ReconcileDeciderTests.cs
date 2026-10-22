using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class ReconcileDeciderTests
{
    private const string Confirm = nameof(ReconcileDecision.Confirm);
    private const string Resend = nameof(ReconcileDecision.Resend);
    private const string Fail = nameof(ReconcileDecision.Fail);

    private const int LimitW = 3700;

    private static readonly RetryPolicyOptions Options = new();

    // One row per (type × status) cell of design §5. Power is what a fresh observation reports.
    private static readonly (CommandType Type, DeviceStatus Status, int? PowerW, string Expected)[] Cells =
    [
        (CommandType.StartCharging, DeviceStatus.Charging, 7400, Confirm),
        (CommandType.StartCharging, DeviceStatus.Available, 0, Resend),
        (CommandType.StartCharging, DeviceStatus.Faulted, 0, Fail),
        (CommandType.StartCharging, DeviceStatus.Offline, null, Resend),
        (CommandType.StartCharging, DeviceStatus.Unknown, null, Resend),
        (CommandType.StopCharging, DeviceStatus.Charging, 7400, Resend),
        (CommandType.StopCharging, DeviceStatus.Available, 0, Confirm),
        (CommandType.StopCharging, DeviceStatus.Faulted, 0, Confirm),
        (CommandType.StopCharging, DeviceStatus.Offline, null, Resend),
        (CommandType.StopCharging, DeviceStatus.Unknown, null, Resend),
        (CommandType.SetPowerLimit, DeviceStatus.Charging, LimitW, Confirm),
        (CommandType.SetPowerLimit, DeviceStatus.Available, 0, Confirm),
        (CommandType.SetPowerLimit, DeviceStatus.Faulted, 0, Fail),
        (CommandType.SetPowerLimit, DeviceStatus.Offline, null, Resend),
        (CommandType.SetPowerLimit, DeviceStatus.Unknown, null, Resend),
    ];

    public static TheoryData<CommandType, DeviceStatus, int?, string> Table()
    {
        var data = new TheoryData<CommandType, DeviceStatus, int?, string>();
        foreach (var (type, status, powerW, expected) in Cells)
        {
            data.Add(type, status, powerW, expected);
        }

        return data;
    }

    [Fact]
    public void Every_type_and_status_pair_has_exactly_one_row()
    {
        var all = Enum.GetValues<CommandType>().SelectMany(_ => Enum.GetValues<DeviceStatus>(), (t, s) => (t, s)).Order();

        Assert.Equal(all, Cells.Select(c => (c.Type, c.Status)).Order());
    }

    [Fact]
    public void Reconcile_decisions_are_a_closed_set()
    {
        Assert.Equal(
            [Confirm, Fail, Resend],
            typeof(ReconcileDecision).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(ReconcileDecision))).Select(t => t.Name).Order());
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Fresh_observation_decides_by_type_and_status(CommandType type, DeviceStatus status, int? powerW, string expected)
    {
        var command = Reconciling(type);

        var decision = ReconcileDecider.Decide(command, Fresh(command, status, powerW), Options);

        Assert.Equal(expected, decision.GetType().Name);
    }

    [Theory]
    [InlineData(3750, Confirm)] // within max(5% of 3,700 W, 100 W) = 185 W
    [InlineData(3885, Confirm)] // exactly at the tolerance
    [InlineData(3886, Resend)]
    [InlineData(7400, Resend)] // the limit was ignored
    public void Power_limit_confirms_within_the_tolerance(int powerW, string expected)
    {
        var command = Reconciling(CommandType.SetPowerLimit);

        var decision = ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Charging, powerW), Options);

        Assert.Equal(expected, decision.GetType().Name);
    }

    [Fact]
    public void Small_power_limit_uses_the_minimum_tolerance()
    {
        // 5% of 1,000 W is 50 W, so the 100 W floor applies.
        var command = Reconciling(CommandType.SetPowerLimit, maxPowerW: 1000);

        Assert.IsType<ReconcileDecision.Confirm>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Charging, 1100), Options));
        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Charging, 1101), Options));
    }

    [Fact]
    public void Power_limit_charging_without_a_power_reading_resends()
    {
        var command = Reconciling(CommandType.SetPowerLimit);

        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Charging, null), Options));
    }

    [Fact]
    public void Observation_from_before_the_send_proves_nothing()
    {
        var command = InState(CommandState.Published);
        command.Accept(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));
        command.TimeOut(new DateTimeOffset(2026, 10, 12, 1, 1, 0, TimeSpan.Zero));
        var stale = new DeviceObservation(DeviceStatus.Charging, 7400, new DateTimeOffset(2026, 10, 12, 0, 59, 30, TimeSpan.Zero));

        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, stale, Options));
    }

    [Fact]
    public void Observation_at_the_send_time_proves_nothing()
    {
        var command = Reconciling(CommandType.StartCharging);
        var atSend = new DeviceObservation(DeviceStatus.Charging, 7400, command.LastSentAt!.Value);

        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, atSend, Options));
    }

    [Fact]
    public void Missing_observation_resends()
    {
        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(Reconciling(CommandType.StartCharging), null, Options));
    }

    [Fact]
    public void Faulted_device_fails_with_a_reason()
    {
        var command = Reconciling(CommandType.StartCharging);

        var fail = Assert.IsType<ReconcileDecision.Fail>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Faulted, 0), Options));

        Assert.Contains("Faulted", fail.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resend_with_no_attempts_left_becomes_fail()
    {
        var command = ReconcilingAfterAttempts(Options.MaxAttempts);

        var fail = Assert.IsType<ReconcileDecision.Fail>(ReconcileDecider.Decide(command, null, Options));

        command.GiveUp(Later(command), fail.Reason);
        Assert.Equal(CommandState.DeadLettered, command.State);
    }

    [Fact]
    public void Resend_with_one_attempt_left_still_resends()
    {
        var command = ReconcilingAfterAttempts(Options.MaxAttempts - 1);

        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, null, Options));
    }

    [Fact]
    public void Confirm_is_not_limited_by_attempts()
    {
        var command = ReconcilingAfterAttempts(Options.MaxAttempts);

        Assert.IsType<ReconcileDecision.Confirm>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Charging, 7400), Options));
    }

    [Fact]
    public void Applying_a_resend_decision_keeps_the_id_and_key()
    {
        var command = Reconciling(CommandType.StartCharging);
        var id = command.Id;
        var key = command.IdempotencyKey;

        Assert.IsType<ReconcileDecision.Resend>(ReconcileDecider.Decide(command, Fresh(command, DeviceStatus.Available, 0), Options));
        command.Resend(Later(command));

        Assert.Equal(CommandState.Published, command.State);
        Assert.Equal(id, command.Id);
        Assert.Equal(key, command.IdempotencyKey);
    }

    [Theory]
    [InlineData(CommandState.Sent)]
    [InlineData(CommandState.Confirmed)]
    public void Only_reconciling_commands_can_be_decided(CommandState state)
    {
        Assert.Throws<ArgumentException>(() => ReconcileDecider.Decide(InState(state), null, Options));
    }

    private static Command Reconciling(CommandType type, int? maxPowerW = null) =>
        InState(CommandState.Reconciling, Request(type, maxPowerW));

    // A reconciling start command that has used the given number of send attempts (all but the last transient).
    private static Command ReconcilingAfterAttempts(int attempts)
    {
        var command = InState(CommandState.Published);
        for (var i = 1; i < attempts; i++)
        {
            Fire(command, CommandTrigger.RetryLater, Later(command));
        }

        command.Accept(Later(command));
        command.TimeOut(Later(command));
        Assert.Equal(attempts, command.Attempts);
        return command;
    }

    private static DeviceObservation Fresh(Command command, DeviceStatus status, int? powerW) =>
        new(status, powerW, Later(command));
}
