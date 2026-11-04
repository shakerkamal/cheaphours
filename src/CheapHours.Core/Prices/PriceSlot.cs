namespace CheapHours.Core.Prices;

/// <summary>One bidding zone's day-ahead price for one 15-minute slot (D4). Prices may be negative.</summary>
public sealed record PriceSlot
{
    /// <summary>Creates a price slot.</summary>
    /// <param name="zone">The bidding zone, for example <c>DE-LU</c>.</param>
    /// <param name="startUtc">The slot start in UTC, on a 15-minute boundary.</param>
    /// <param name="priceEurPerMwh">The price in EUR/MWh.</param>
    /// <exception cref="ArgumentException">The zone is blank, or the start is not UTC or not on the 15-minute grid.</exception>
    public PriceSlot(string zone, DateTimeOffset startUtc, decimal priceEurPerMwh)
    {
        Zone = Guard.NotBlank(zone);
        if (!SlotGrid.IsAligned(startUtc))
        {
            throw new ArgumentException($"A price slot must start on a 15-minute boundary, but starts at {startUtc:O}.", nameof(startUtc));
        }

        StartUtc = startUtc;
        PriceEurPerMwh = priceEurPerMwh;
    }

    /// <summary>Gets the bidding zone.</summary>
    public string Zone { get; }

    /// <summary>Gets the slot start (UTC).</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>Gets the slot end (UTC).</summary>
    public DateTimeOffset EndUtc => StartUtc + SlotGrid.SlotLength;

    /// <summary>Gets the price in EUR/MWh.</summary>
    public decimal PriceEurPerMwh { get; }
}
