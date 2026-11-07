using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Outbox;
using CheapHours.Persistence.Plans;
using CheapHours.Planning;

namespace CheapHours.IntegrationTests.Persistence;

/// <summary>Commands and outbox entries for the store tests; ids carry a per-test suffix.</summary>
internal static class CommandSamples
{
    public static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 22, 0, 0, TimeSpan.Zero);

    public static StoredCommand Command(
        string suffix,
        string name = "a",
        CommandType type = CommandType.StartCharging,
        DateTimeOffset? dueAt = null,
        string? device = null,
        PlanId? planId = null,
        int? planVersion = null,
        OwnerId? owner = null)
    {
        var request = new DeviceCommand(
            CommandId.Parse($"cmd_{name}{suffix}"),
            DeviceId.Parse(device ?? $"dev_wb{suffix}"),
            type,
            $"key_{name}{suffix}",
            type == CommandType.SetPowerLimit ? 3700 : null);
        return new StoredCommand(
            Core.Commands.Command.Create(request, Now, dueAt),
            owner ?? Owner,
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            planId,
            planVersion);
    }

    /// <summary>The commands a plan version asks for, built by the real schedule, with one announcement each.</summary>
    public static CommandBatch ForPlan(StoredPlan version, DeviceId charger, DateTimeOffset now)
    {
        var commands = PlanCommandSchedule.For(version.Plan, previous: null, chargerCharging: false, now)
            .Select(s => new StoredCommand(
                Core.Commands.Command.Create(
                    new DeviceCommand(CommandId.Parse($"cmd_{Guid.NewGuid():N}"), charger, s.Type, s.IdempotencyKey),
                    now,
                    s.DueAt),
                version.Plan.OwnerId,
                null,
                version.Plan.Id,
                version.Plan.Version))
            .ToArray();
        return new CommandBatch(commands, [.. commands.Select(c => Announcement(c, createdAt: now))]);
    }

    public static OutboxEntry Announcement(StoredCommand command, DateTimeOffset? availableAt = null, DateTimeOffset? createdAt = null) => new(
        Guid.NewGuid(),
        "CommandRequested",
        1,
        $$"""{"commandId":"{{command.Id}}"}""",
        command.Traceparent,
        OutboxRoute.Events,
        "CommandRequested",
        availableAt ?? command.Command.DueAt,
        createdAt ?? Now,
        command.Id);
}
