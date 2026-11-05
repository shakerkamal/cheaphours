using System.Collections.Concurrent;
using CheapHours.Persistence;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// One indexed database per test class, created on first use. Creating collections and indexes is the most
/// contended work on the shared MongoDB, so classes whose tests keep apart by their own ids (zones, lease names,
/// goals, devices) share one database instead of building one per test.
/// </summary>
internal static class SharedDatabases
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<IMongoDatabase>>> Databases = new(StringComparer.Ordinal);

    /// <summary>Returns the indexed database of <paramref name="owner"/>, creating it once.</summary>
    /// <param name="mongo">The MongoDB fixture.</param>
    /// <param name="owner">The test class name.</param>
    /// <returns>The database.</returns>
    public static Task<IMongoDatabase> GetAsync(MongoFixture mongo, string owner) =>
        Databases.GetOrAdd(owner, _ => new Lazy<Task<IMongoDatabase>>(async () =>
        {
            var database = mongo.Client.GetDatabase(MongoFixture.NewDatabaseName(owner));
            await MongoIndexes.EnsureAsync(database, CancellationToken.None);
            return database;
        })).Value;

    /// <summary>Returns a short suffix that keeps one test's ids apart from the others in a shared database.</summary>
    /// <returns>Eight hex characters.</returns>
    public static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];
}
