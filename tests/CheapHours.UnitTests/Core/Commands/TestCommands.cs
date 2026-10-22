using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.UnitTests.Core.Commands;

// Builds commands and drives them into a given state through the public triggers only.
internal static class TestCommands
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 10, 12, 0, 59, 58, TimeSpan.Zero);

    public static DeviceCommand Request(CommandType type = CommandType.StartCharging, int? maxPowerW = null) =>
        new(
            CommandId.Parse("cmd_01JB7Z8K2Q"),
            DeviceId.Parse("dev_wallbox_01"),
            type,
            $"plan_8812:v3:slot_2026-10-12T01:00Z:{type}",
            maxPowerW ?? (type == CommandType.SetPowerLimit ? 3700 : null));

    public static Command New(DeviceCommand? request = null) => Command.Create(request ?? Request(), CreatedAt);

    // The shortest trigger path from Pending to each state.
    public static IReadOnlyList<CommandTrigger> PathTo(CommandState state) => state switch
    {
        CommandState.Pending => [],
        CommandState.Published => [CommandTrigger.Publish],
        CommandState.Sent => [CommandTrigger.Publish, CommandTrigger.Accept],
        CommandState.Confirmed => [CommandTrigger.Publish, CommandTrigger.Accept, CommandTrigger.Confirm],
        CommandState.Reconciling => [CommandTrigger.Publish, CommandTrigger.Accept, CommandTrigger.TimeOut],
        CommandState.Rejected => [CommandTrigger.Publish, CommandTrigger.Reject],
        CommandState.DeadLettered => [CommandTrigger.Publish, CommandTrigger.GiveUp],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static Command InState(CommandState state, DeviceCommand? request = null)
    {
        var command = New(request);
        foreach (var trigger in PathTo(state))
        {
            Fire(command, trigger, Later(command));
        }

        return command;
    }

    // One second after the command's last history entry.
    public static DateTimeOffset Later(Command command) => command.History[^1].At.AddSeconds(1);

    public static void Fire(Command command, CommandTrigger trigger, DateTimeOffset at)
    {
        switch (trigger)
        {
            case CommandTrigger.Publish: command.Publish(at); break;
            case CommandTrigger.Accept: command.Accept(at, "vendor-ref"); break;
            case CommandTrigger.Reject: command.Reject(at, "refused by vendor"); break;
            case CommandTrigger.RetryLater: command.RetryLater(at, at.AddSeconds(5), "vendor returned 503"); break;
            case CommandTrigger.GiveUp: command.GiveUp(at, "retries exhausted"); break;
            case CommandTrigger.Confirm: command.Confirm(at); break;
            case CommandTrigger.TimeOut: command.TimeOut(at); break;
            case CommandTrigger.Resend: command.Resend(at); break;
            default: throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null);
        }
    }
}
