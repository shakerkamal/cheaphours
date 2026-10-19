using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging.Contracts;

namespace CheapHours.UnitTests.Messaging;

/// <summary>One valid instance of every message type.</summary>
internal static class MessageSamples
{
    public const string Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    public const string Key = "plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging";

    public static readonly DateTimeOffset At = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
    public static readonly CommandId Cmd = CommandId.Parse("cmd_01JB7Z8K2Q");
    public static readonly DeviceId Wallbox = DeviceId.Parse("dev_wallbox_01");

    public static Guid NewId() => Guid.CreateVersion7(At);

    public static CommandRequested CommandRequested() => new(NewId(), At, Traceparent, Cmd, Wallbox, Key);

    public static IReadOnlyList<Message> All() =>
    [
        new TelemetryReceived(NewId(), At, Traceparent, Wallbox, "ocpp", [new DeviceReading("CP-01", At, 7400, 12_345, 42.5m), new DeviceReading("CP-01", At.AddSeconds(30), 7300, 12_406)]),
        new PricesPublished(NewId(), At, null, "DE-LU", new DateOnly(2026, 10, 13), 96),
        CommandRequested(),
        new CommandSent(NewId(), At, Traceparent, Cmd, Wallbox, Key, "ocpp-msg-4471"),
        new CommandConfirmed(NewId(), At, Traceparent, Cmd, Wallbox, Key),
        new GatewayCommand(NewId(), At, Traceparent, "CP-01", "gw-0", new DeviceCommand(Cmd, Wallbox, CommandType.StartCharging, Key, 7400)),
        new GatewayCommandResult(NewId(), At, Traceparent, Cmd, new SendResult.Transient("charger busy", TimeSpan.FromSeconds(30))),
    ];
}
