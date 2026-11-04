using CheapHours.Core.Prices;

namespace CheapHours.UnitTests.Core;

public sealed class PriceSlotTests
{
    private static readonly DateTimeOffset ThreeAm = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Negative_price_is_a_valid_slot()
    {
        var slot = new PriceSlot("DE-LU", ThreeAm, -4.2m);

        Assert.Equal(-4.2m, slot.PriceEurPerMwh);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 3, 15, 0, TimeSpan.Zero), slot.EndUtc);
        Assert.Equal("DE-LU", slot.Zone);
    }

    [Fact]
    public void Slot_off_the_grid_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PriceSlot("DE-LU", ThreeAm.AddMinutes(5), 80m));
    }

    [Fact]
    public void Slot_needs_utc_and_a_zone()
    {
        Assert.Throws<ArgumentException>(() => new PriceSlot("DE-LU", new DateTimeOffset(2026, 11, 2, 4, 0, 0, TimeSpan.FromHours(1)), 80m));
        Assert.Throws<ArgumentException>(() => new PriceSlot(" ", ThreeAm, 80m));
    }
}
