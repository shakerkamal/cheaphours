using CheapHours.Core;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Prices;
using CheapHours.Persistence.Prices;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Api.Prices;

/// <summary>Handler for <c>GET /v1/prices</c> (planning-api spec).</summary>
internal static class PriceEndpoints
{
    /// <summary>
    /// The stored slots of <paramref name="zone"/> whose start lies in the range widened to the 15-minute grid
    /// (<paramref name="from"/> down, <paramref name="to"/> up), oldest first, with cursor paging. Slots without a
    /// price are not stored, so they are left out.
    /// </summary>
    /// <param name="zone">The bidding zone; must be the configured one.</param>
    /// <param name="from">The start of the range (UTC).</param>
    /// <param name="to">The end of the range (UTC).</param>
    /// <param name="cursor">The cursor from the previous page, if any.</param>
    /// <param name="limit">The page size, if sent.</param>
    /// <param name="prices">The price store.</param>
    /// <param name="options">The price settings.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with a page of slots, or 400.</returns>
    public static async Task<IResult> ListAsync(
        string zone,
        DateTimeOffset from,
        DateTimeOffset to,
        string? cursor,
        int? limit,
        IPriceStore prices,
        IOptions<PricesOptions> options,
        CancellationToken cancellationToken)
    {
        var rules = new QueryRules();
        rules.Zone(zone, options.Value.Zone);
        var take = rules.Limit(limit);
        rules.Range(from, to);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        // The cursor is checked against the widened range, so the range must be valid first.
        var start = SlotGrid.Floor(from);
        var end = SlotGrid.Ceiling(to);
        var pageStart = rules.TelemetryCursor(cursor, start, end);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        if (pageStart >= end)
        {
            return TypedResults.Ok(new Page<PriceSlotDto>([], null));
        }

        // One extra slot tells whether another page follows.
        var rows = await prices.ReadAsync(zone, pageStart, end, afterUtc: null, take + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).Select(p => new PriceSlotDto(p.Zone, p.StartUtc, p.EndUtc, p.PriceEurPerMwh)).ToList();
        var next = rows.Count > take ? PageCursor.ForBucket(rows[take - 1].StartUtc) : null;
        return TypedResults.Ok(new Page<PriceSlotDto>(page, next));
    }
}
