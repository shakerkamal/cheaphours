namespace CheapHours.Persistence.Outbox;

/// <summary>Where the relay publishes an outbox entry.</summary>
public enum OutboxRoute
{
    /// <summary>To the events exchange, with the entry's destination as routing key.</summary>
    Events,

    /// <summary>Through the default exchange straight to the queue the destination names.</summary>
    Queue,
}
