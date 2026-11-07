using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Commands;

/// <summary>
/// Maps between <see cref="StoredCommand"/> and <see cref="CommandDocument"/>. Reading goes through
/// <see cref="Command.Restore"/>, so a stored command can only be one the transition table could produce; only our
/// code writes these documents, so a document that breaks that is a bug and throws.
/// </summary>
internal static class CommandDocumentMapper
{
    public static CommandDocument ToDocument(StoredCommand stored, long version)
    {
        var command = stored.Command;
        var request = command.Request;
        return new CommandDocument
        {
            Id = request.Id.Value,
            OwnerId = stored.OwnerId.Value,
            DeviceId = request.DeviceId.Value,
            Type = request.Type,
            MaxPowerW = request.MaxPowerW,
            IdempotencyKey = request.IdempotencyKey,
            DueAt = UtcTime.ToBson(command.DueAt),
            PlanId = stored.PlanId?.Value,
            PlanVersion = stored.PlanVersion,
            Traceparent = stored.Traceparent,
            CreatedAt = UtcTime.ToBson(stored.CreatedAt),
            State = command.State,
            Attempts = command.Attempts,
            NextAttemptAt = command.NextAttemptAt is { } next ? UtcTime.ToBson(next) : null,
            History = [.. command.History.Select(h => new CommandDocument.HistoryEntryDocument
            {
                State = h.State,
                At = UtcTime.ToBson(h.At),
                VendorRef = h.VendorReference,
                Reason = h.Reason,
            })],
            Claim = stored.Claim is { } claim ? new CommandDocument.ClaimDocument { By = claim.By, Until = UtcTime.ToBson(claim.Until) } : null,
            Announcements = stored.Announcements,
            LastAnnouncedAt = stored.LastAnnouncedAt is { } announced ? UtcTime.ToBson(announced) : null,
            AlreadyInEffect = stored.AlreadyInEffect,
            Version = version,
        };
    }

    public static StoredCommand ToStored(CommandDocument d)
    {
        var request = new DeviceCommand(CommandId.Parse(d.Id), DeviceId.Parse(d.DeviceId), d.Type, d.IdempotencyKey, d.MaxPowerW);
        var history = d.History.Select(h => new CommandHistoryEntry(h.State, UtcTime.FromBson(h.At), h.VendorRef, h.Reason)).ToArray();
        var command = Command.Restore(request, UtcTime.FromBson(d.DueAt), history, UtcTime.FromBson(d.NextAttemptAt));
        if (command.State != d.State || command.Attempts != d.Attempts)
        {
            throw new InvalidOperationException(
                $"Command {d.Id} stores state {d.State} with {d.Attempts} attempts, but its history gives {command.State} with {command.Attempts}.");
        }

        return new StoredCommand(
            command,
            OwnerId.Parse(d.OwnerId),
            d.Traceparent,
            d.PlanId is { } planId ? PlanId.Parse(planId) : null,
            d.PlanVersion,
            d.Claim is { } claim ? new CommandClaim(claim.By, UtcTime.FromBson(claim.Until)) : null,
            d.Announcements,
            UtcTime.FromBson(d.LastAnnouncedAt),
            d.AlreadyInEffect,
            d.Version);
    }
}
