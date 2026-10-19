using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>What happened when a gateway replica sent a <see cref="GatewayCommand"/> to its charger.</summary>
public sealed record GatewayCommandResult : Message
{
    /// <summary>The message type name.</summary>
    public const string TypeName = nameof(GatewayCommandResult);

    /// <summary>The schema version this code writes and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates the message.</summary>
    /// <param name="messageId">The message's unique id.</param>
    /// <param name="occurredAt">When the charger answered, or the send failed (UTC).</param>
    /// <param name="traceparent">The W3C trace context, if any.</param>
    /// <param name="commandId">The command this answers.</param>
    /// <param name="outcome">The send result.</param>
    public GatewayCommandResult(Guid messageId, DateTimeOffset occurredAt, string? traceparent, CommandId commandId, SendResult outcome)
        : base(messageId, occurredAt, traceparent)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        CommandId = Require.NotDefault(commandId);
        Outcome = outcome;
    }

    /// <inheritdoc/>
    public override string MessageType => TypeName;

    /// <inheritdoc/>
    public override int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the command this answers.</summary>
    public CommandId CommandId { get; }

    /// <summary>Gets the send result.</summary>
    public SendResult Outcome { get; }
}
