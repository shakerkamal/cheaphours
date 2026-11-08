using MongoDB.Driver;

namespace CheapHours.Host.Commands;

/// <summary>Tells a MongoDB outage, which pauses a consumer without spending its retries, from a failing message.</summary>
internal static class CommandStoreOutage
{
    /// <summary>Returns whether <paramref name="ex"/> means MongoDB cannot be reached right now.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> for timeouts, lost connections and a primary that is not available.</returns>
    public static bool Is(Exception ex) =>
        ex is TimeoutException or MongoConnectionException or MongoNotPrimaryException or MongoNodeIsRecoveringException;
}
