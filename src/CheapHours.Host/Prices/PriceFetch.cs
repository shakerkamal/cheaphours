namespace CheapHours.Host.Prices;

/// <summary>What asking the price source for one day gave (design §2). A closed set; no outcome is an exception.</summary>
internal abstract record PriceFetch
{
    private PriceFetch()
    {
    }

    /// <summary>The source answered with a valid day, complete or not.</summary>
    /// <param name="Day">The day's priced slots.</param>
    /// <param name="Deprecated">Whether the source marks its endpoint deprecated.</param>
    public sealed record Published(PriceDay Day, bool Deprecated) : PriceFetch;

    /// <summary>The source has nothing for the day yet (404).</summary>
    public sealed record NotPublished : PriceFetch;

    /// <summary>The source asked us to slow down (429); the run ends.</summary>
    public sealed record RateLimited : PriceFetch;

    /// <summary>The source answered with a response that broke a rule; nothing from it may be stored.</summary>
    /// <param name="Rule">The first broken rule.</param>
    public sealed record Rejected(PriceRule Rule) : PriceFetch;

    /// <summary>The request failed after its retries.</summary>
    /// <param name="Reason">A status code or an exception type name; never a body.</param>
    public sealed record Failed(string Reason) : PriceFetch;
}
