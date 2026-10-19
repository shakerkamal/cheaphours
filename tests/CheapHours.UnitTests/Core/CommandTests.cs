using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.UnitTests.Core;

public sealed class CommandTests
{
    private const string Key = "plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging";
    private static readonly CommandId Cmd = CommandId.Parse("cmd_01JB7Z8K2Q");
    private static readonly DeviceId Wallbox = DeviceId.Parse("dev_wallbox_01");
    private static readonly DateTimeOffset At = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Command_types_are_the_closed_set()
    {
        Assert.Equal(
            ["StartCharging", "StopCharging", "SetPowerLimit"],
            Enum.GetNames<CommandType>());
    }

    [Fact]
    public void Start_with_optional_power_is_created()
    {
        var command = new DeviceCommand(Cmd, Wallbox, CommandType.StartCharging, Key, maxPowerW: 7400);

        Assert.Equal(Wallbox, command.DeviceId);
        Assert.Equal(7400, command.MaxPowerW);
        Assert.Equal(Capabilities.StartStop, command.RequiredCapability);
    }

    [Fact]
    public void Power_limit_requires_the_set_power_limit_capability()
    {
        var command = new DeviceCommand(Cmd, Wallbox, CommandType.SetPowerLimit, Key, maxPowerW: 3700);

        Assert.Equal(Capabilities.SetPowerLimit, command.RequiredCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Power_limit_without_a_positive_value_is_refused(int maxPowerW)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceCommand(Cmd, Wallbox, CommandType.SetPowerLimit, Key, maxPowerW));
    }

    [Fact]
    public void Power_limit_without_any_value_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DeviceCommand(Cmd, Wallbox, CommandType.SetPowerLimit, Key));
    }

    [Fact]
    public void Stop_with_a_power_value_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DeviceCommand(Cmd, Wallbox, CommandType.StopCharging, Key, maxPowerW: 7400));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_idempotency_key_is_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => new DeviceCommand(Cmd, Wallbox, CommandType.StopCharging, key));
    }

    [Fact]
    public void Undefined_command_type_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceCommand(Cmd, Wallbox, (CommandType)99, Key));
    }

    [Fact]
    public void Send_result_has_exactly_three_outcomes()
    {
        Assert.Equal(["Accepted", "Rejected", "Transient"], ClosedSubtypes(typeof(SendResult)));

        // Records always get a protected copy constructor (C# requires it on a non-sealed record); it needs an
        // existing outcome, so the private constructor is what keeps new outcomes from being defined.
        var creating = typeof(SendResult)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(c => c.GetParameters() is not [{ ParameterType: var p }] || p != typeof(SendResult));
        Assert.All(creating, c => Assert.True(c.IsPrivate, "SendResult's constructor must be private so no other outcome can exist"));
    }

    [Fact]
    public void Send_result_outcomes_carry_their_data()
    {
        SendResult[] results = [new SendResult.Accepted("ocpp-msg-4471"), new SendResult.Rejected("capability-not-supported"), new SendResult.Transient("vendor returned 429", TimeSpan.FromSeconds(30))];

        var described = results.Select(r => r switch
        {
            SendResult.Accepted a => $"accepted {a.VendorReference}",
            SendResult.Rejected j => $"rejected {j.Reason}",
            SendResult.Transient t => $"transient {t.Reason} {t.RetryAfter}",
            _ => throw new InvalidOperationException("unreachable"),
        });

        Assert.Equal(["accepted ocpp-msg-4471", "rejected capability-not-supported", "transient vendor returned 429 00:00:30"], described);
    }

    [Fact]
    public void Rejected_and_transient_need_a_reason()
    {
        Assert.Throws<ArgumentException>(() => new SendResult.Rejected(" "));
        Assert.Throws<ArgumentException>(() => new SendResult.Transient(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SendResult.Transient("x", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Device_events_are_a_closed_set()
    {
        Assert.Equal(
            ["Connected", "DeviceRemoved", "Disconnected", "MalformedInput", "ReadingReported", "StatusChanged"],
            ClosedSubtypes(typeof(DeviceEvent)));
    }

    [Fact]
    public void Device_events_require_utc()
    {
        var local = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new DeviceEvent.Connected("CP-01", local));
        Assert.Equal(At, new DeviceEvent.ReadingReported(new DeviceReading("CP-01", At, 0, 0)).At);
    }

    [Fact]
    public void Command_states_match_D7()
    {
        Assert.Equal(
            ["Pending", "Published", "Sent", "Confirmed", "Reconciling", "Rejected", "DeadLettered"],
            Enum.GetNames<CommandState>());
    }

    [Fact]
    public void Exactly_confirmed_rejected_and_dead_lettered_are_terminal()
    {
        var terminal = Enum.GetValues<CommandState>().Where(s => s.IsTerminal());

        Assert.Equal([CommandState.Confirmed, CommandState.Rejected, CommandState.DeadLettered], terminal);
    }

    private static string[] ClosedSubtypes(Type baseType) =>
        baseType.Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(baseType))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
