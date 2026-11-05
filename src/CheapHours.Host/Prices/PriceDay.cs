using CheapHours.Core.Prices;

namespace CheapHours.Host.Prices;

/// <summary>
/// One zone's prices for one Berlin day as far as they are published: the priced slots in time order and the
/// number of slots the day has. The day is complete when every slot has a price.
/// </summary>
/// <param name="Zone">The bidding zone.</param>
/// <param name="Day">The Berlin day.</param>
/// <param name="SlotCount">The day's slot count (92, 96 or 100).</param>
/// <param name="Slots">The priced slots, in time order.</param>
internal sealed record PriceDay(string Zone, DateOnly Day, int SlotCount, IReadOnlyList<PriceSlot> Slots)
{
    /// <summary>Gets whether every slot of the day has a price.</summary>
    public bool IsComplete => Slots.Count == SlotCount;
}
