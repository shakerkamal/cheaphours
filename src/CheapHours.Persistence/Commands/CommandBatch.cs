using CheapHours.Persistence.Outbox;

namespace CheapHours.Persistence.Commands;

/// <summary>New commands and the outbox entries that announce them, written together in one transaction.</summary>
/// <param name="Commands">The commands, each at version 0.</param>
/// <param name="Announcements">One outbox entry per command.</param>
public sealed record CommandBatch(IReadOnlyList<StoredCommand> Commands, IReadOnlyList<OutboxEntry> Announcements)
{
    /// <summary>Gets a batch with nothing in it, for a plan version without slots.</summary>
    public static CommandBatch None { get; } = new([], []);
}
