namespace CheapHours.Persistence.Commands;

/// <summary>A dispatcher's hold on a command while it hands it to the device; lapses at <see cref="Until"/>.</summary>
/// <param name="By">Who holds it, for example the worker replica id.</param>
/// <param name="Until">When the hold lapses (UTC).</param>
public sealed record CommandClaim(string By, DateTimeOffset Until)
{
    /// <summary>Returns whether the claim still holds at <paramref name="now"/>.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><see langword="true"/> before <see cref="Until"/>.</returns>
    public bool IsLiveAt(DateTimeOffset now) => Until > now;
}
