using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class CommandConfirmationTests
{
    public static TheoryData<CommandType, DeviceStatus> AllPairs()
    {
        var data = new TheoryData<CommandType, DeviceStatus>();
        foreach (var type in Enum.GetValues<CommandType>())
        {
            foreach (var status in Enum.GetValues<DeviceStatus>())
            {
                data.Add(type, status);
            }
        }

        return data;
    }

    [Fact]
    public void Start_is_confirmed_by_charging_reported_after_the_send()
    {
        var command = InState(CommandState.Sent);

        Assert.True(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Charging, seconds: 5), alreadyInEffect: false));
    }

    [Fact]
    public void Report_from_before_the_send_does_not_confirm()
    {
        var command = InState(CommandState.Sent);

        Assert.False(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Charging, seconds: -5), alreadyInEffect: false));
    }

    [Fact]
    public void Report_at_the_send_time_does_not_confirm()
    {
        var command = InState(CommandState.Sent);

        Assert.False(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Charging, seconds: 0), alreadyInEffect: false));
    }

    [Fact]
    public void Already_in_effect_is_confirmed_by_an_older_matching_report()
    {
        var command = InState(CommandState.Sent);

        Assert.True(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Charging, seconds: -60), alreadyInEffect: true));
    }

    [Fact]
    public void Already_in_effect_still_needs_the_requested_state()
    {
        var command = InState(CommandState.Sent);

        Assert.False(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Available, seconds: -60), alreadyInEffect: true));
    }

    [Fact]
    public void Stop_is_confirmed_by_not_charging()
    {
        var command = InState(CommandState.Sent, Request(CommandType.StopCharging));

        Assert.True(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Available, seconds: 5), alreadyInEffect: false));
        Assert.False(CommandConfirmation.IsConfirmed(command, Observed(command, DeviceStatus.Charging, seconds: 5), alreadyInEffect: false));
    }

    [Theory]
    [InlineData(DeviceStatus.Charging, 3700, true)]
    [InlineData(DeviceStatus.Charging, 3885, true)]
    [InlineData(DeviceStatus.Charging, 3886, false)]
    [InlineData(DeviceStatus.Charging, null, false)]
    [InlineData(DeviceStatus.Available, null, true)]
    public void Power_limit_is_confirmed_within_the_tolerance_or_when_not_charging(DeviceStatus status, int? powerW, bool expected)
    {
        var command = InState(CommandState.Sent, Request(CommandType.SetPowerLimit, 3700));

        Assert.Equal(expected, CommandConfirmation.IsConfirmed(command, Observed(command, status, seconds: 5, powerW), alreadyInEffect: false));
    }

    [Theory]
    [InlineData(DeviceStatus.Offline)]
    [InlineData(DeviceStatus.Unknown)]
    public void Offline_or_unknown_never_confirms(DeviceStatus status)
    {
        var command = InState(CommandState.Sent);

        Assert.False(CommandConfirmation.IsConfirmed(command, Observed(command, status, seconds: 5), alreadyInEffect: true));
    }

    [Fact]
    public void No_observation_does_not_confirm()
    {
        Assert.False(CommandConfirmation.IsConfirmed(InState(CommandState.Sent), observation: null, alreadyInEffect: true));
    }

    [Theory]
    [InlineData(CommandState.Published)]
    [InlineData(CommandState.Reconciling)]
    [InlineData(CommandState.Confirmed)]
    public void Only_sent_commands_can_be_checked(CommandState state)
    {
        var command = InState(state);

        Assert.Throws<ArgumentException>(() => CommandConfirmation.IsConfirmed(command, null, alreadyInEffect: false));
    }

    // Confirmation and reconcile share one rule: a fresh observation confirms exactly when the decider confirms.
    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Fresh_observation_confirms_exactly_when_the_decider_confirms(CommandType type, DeviceStatus status)
    {
        var sent = InState(CommandState.Sent, Request(type));
        var reconciling = InState(CommandState.Reconciling, Request(type));
        var observation = new DeviceObservation(status, 3000, sent.History[^1].At.AddSeconds(5));
        var lateObservation = new DeviceObservation(status, 3000, reconciling.History[^1].At.AddSeconds(5));

        var decided = ReconcileDecider.Decide(reconciling, lateObservation, new RetryPolicyOptions());

        Assert.Equal(decided is ReconcileDecision.Confirm, CommandConfirmation.IsConfirmed(sent, observation, alreadyInEffect: false));
    }

    [Fact]
    public void Already_in_effect_reference_is_stable()
    {
        Assert.Equal("already-in-effect", CommandVendorReferences.AlreadyInEffect);
    }

    private static DeviceObservation Observed(Command command, DeviceStatus status, int seconds, int? powerW = null) =>
        new(status, powerW, command.LastSentAt!.Value.AddSeconds(seconds));
}
