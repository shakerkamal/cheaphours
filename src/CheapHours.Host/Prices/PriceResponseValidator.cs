using CheapHours.Core.Prices;

namespace CheapHours.Host.Prices;

/// <summary>The rule a price response broke; the first broken rule names the rejection.</summary>
internal enum PriceRule
{
    /// <summary>The unit is not EUR/MWh.</summary>
    Unit,

    /// <summary>The timestamp and price arrays are missing or of different length.</summary>
    Lengths,

    /// <summary>The first timestamp is not the day's first slot.</summary>
    FirstSlot,

    /// <summary>Two timestamps are not exactly 15 minutes apart.</summary>
    Step,

    /// <summary>A timestamp lies outside the requested day.</summary>
    InsideDay,

    /// <summary>A price lies outside −500 to 4000 EUR/MWh.</summary>
    PriceBounds,
}

/// <summary>What checking a price response gave.</summary>
internal abstract record PriceValidation
{
    private PriceValidation()
    {
    }

    /// <summary>The response is valid; <paramref name="Day"/> holds its priced slots.</summary>
    /// <param name="Day">The day's prices.</param>
    public sealed record Accepted(PriceDay Day) : PriceValidation;

    /// <summary>The response broke <paramref name="Rule"/>; nothing from it may be stored.</summary>
    /// <param name="Rule">The first broken rule.</param>
    public sealed record Rejected(PriceRule Rule) : PriceValidation;
}

/// <summary>
/// Checks a day-ahead price response before anything is stored (price-import spec). The unit must be EUR/MWh,
/// the arrays of equal length, the timestamps must start at the day's first slot and step by exactly
/// 15 minutes inside the day, and each price must be missing or within the market's bounds. A response that
/// ends early or holds missing prices is valid and gives a partial day.
/// </summary>
internal static class PriceResponseValidator
{
    /// <summary>The lowest price the day-ahead market allows, in EUR/MWh.</summary>
    public const decimal MinPrice = -500m;

    /// <summary>The highest price the day-ahead market allows, in EUR/MWh.</summary>
    public const decimal MaxPrice = 4000m;

    private const long SlotSeconds = 15 * 60;

    /// <summary>Checks <paramref name="response"/> as the prices of <paramref name="zone"/> on <paramref name="day"/>.</summary>
    /// <param name="zone">The bidding zone requested.</param>
    /// <param name="day">The Berlin day requested.</param>
    /// <param name="response">The parsed response.</param>
    /// <returns>The accepted day or the first broken rule.</returns>
    public static PriceValidation Validate(string zone, DateOnly day, EnergyChartsPriceResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!IsEurPerMwh(response.Unit))
        {
            return new PriceValidation.Rejected(PriceRule.Unit);
        }

        if (response.UnixSeconds is not { } times || response.Price is not { } prices || times.Count != prices.Count)
        {
            return new PriceValidation.Rejected(PriceRule.Lengths);
        }

        var dayStart = BerlinTime.DayStartUtc(day).ToUnixTimeSeconds();
        var dayEnd = BerlinTime.DayEndUtc(day).ToUnixTimeSeconds();
        if (times.Count > 0 && times[0] != dayStart)
        {
            return new PriceValidation.Rejected(PriceRule.FirstSlot);
        }

        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] != SlotSeconds)
            {
                return new PriceValidation.Rejected(PriceRule.Step);
            }
        }

        if (times.Count > 0 && times[^1] + SlotSeconds > dayEnd)
        {
            return new PriceValidation.Rejected(PriceRule.InsideDay);
        }

        if (prices.Any(p => p is < MinPrice or > MaxPrice))
        {
            return new PriceValidation.Rejected(PriceRule.PriceBounds);
        }

        var slots = new List<PriceSlot>(times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            if (prices[i] is { } price)
            {
                slots.Add(new PriceSlot(zone, DateTimeOffset.FromUnixTimeSeconds(times[i]), price));
            }
        }

        return new PriceValidation.Accepted(new PriceDay(zone, day, BerlinTime.SlotCount(day), slots));
    }

    // The source writes "EUR / MWh"; spacing is not significant, the units are.
    private static bool IsEurPerMwh(string? unit) =>
        unit is not null && string.Concat(unit.Where(c => !char.IsWhiteSpace(c))) == "EUR/MWh";
}
