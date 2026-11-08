using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Outbox;

namespace CheapHours.Host.Commands;

/// <summary>Builds the outbox entry that announces a command: a <see cref="CommandRequested"/> on the events exchange.</summary>
internal static class CommandOutbox
{
    /// <summary>Builds the announcement.</summary>
    /// <param name="command">The command.</param>
    /// <param name="now">The current time (UTC); the message's occurrence time.</param>
    /// <param name="availableAt">When the relay may publish it; the command's due time when omitted.</param>
    /// <returns>The outbox entry.</returns>
    public static OutboxEntry Announce(StoredCommand command, DateTimeOffset now, DateTimeOffset? availableAt = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = new CommandRequested(
            Guid.CreateVersion7(now),
            now,
            command.Traceparent,
            command.Id,
            command.Command.Request.DeviceId,
            command.Command.IdempotencyKey);
        var serialized = SerializedMessage.From(message);
        return new OutboxEntry(
            serialized.MessageId,
            serialized.Type,
            serialized.SchemaVersion,
            serialized.Json,
            serialized.Traceparent,
            OutboxRoute.Events,
            CommandRequested.TypeName,
            availableAt ?? command.Command.DueAt,
            now,
            command.Id);
    }

    /// <summary>Turns an outbox entry back into the message it saved, envelope included.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The saved message.</returns>
    public static SerializedMessage ToMessage(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new SerializedMessage(entry.MessageId, entry.Type, entry.SchemaVersion, entry.CreatedAt, entry.Traceparent, entry.Body);
    }
}
