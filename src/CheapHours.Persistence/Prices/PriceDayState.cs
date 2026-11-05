namespace CheapHours.Persistence.Prices;

/// <summary>The import's progress for one zone and Berlin day.</summary>
/// <param name="Zone">The bidding zone.</param>
/// <param name="Day">The Berlin day.</param>
/// <param name="SlotCount">The day's slot count.</param>
/// <param name="CompleteAt">When every slot first had a price, if it has happened.</param>
/// <param name="AnnouncedAt">When the complete day was announced, if it has been.</param>
/// <param name="GaveUpAt">When the import gave up on the day, if it did.</param>
/// <param name="AttemptedAt">When the day was first fetched, if it was.</param>
public sealed record PriceDayState(
    string Zone,
    DateOnly Day,
    int SlotCount,
    DateTimeOffset? CompleteAt,
    DateTimeOffset? AnnouncedAt,
    DateTimeOffset? GaveUpAt,
    DateTimeOffset? AttemptedAt = null)
{
    /// <summary>Gets whether every slot of the day has a price.</summary>
    public bool IsComplete => CompleteAt is not null;
}
