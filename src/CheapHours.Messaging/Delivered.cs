using CheapHours.Messaging.Contracts;

namespace CheapHours.Messaging;

/// <summary>A delivered message.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <param name="Message">The message.</param>
/// <param name="IsRepeat">
/// Whether it may have been handled, fully or in part, before: redelivered by the broker, a retry, or a
/// batch handed over again after an outage.
/// </param>
public sealed record Delivered<TMessage>(TMessage Message, bool IsRepeat)
    where TMessage : Message;
