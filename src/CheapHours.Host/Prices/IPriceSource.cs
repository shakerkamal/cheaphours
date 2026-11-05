namespace CheapHours.Host.Prices;

/// <summary>Where day-ahead prices come from; the import job depends on this seam, not on the source.</summary>
internal interface IPriceSource
{
    /// <summary>Fetches the prices of <paramref name="zone"/> for the Berlin day <paramref name="day"/>.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome; never throws for a source failure.</returns>
    Task<PriceFetch> FetchAsync(string zone, DateOnly day, CancellationToken cancellationToken);
}
