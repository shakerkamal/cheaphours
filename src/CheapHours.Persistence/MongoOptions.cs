namespace CheapHours.Persistence;

/// <summary>
/// MongoDB settings: section <c>Mongo</c>, with the connection string taken from
/// <c>ConnectionStrings:Mongo</c> when that is set.
/// </summary>
public sealed class MongoOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Mongo";

    /// <summary>The name under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Mongo";

    /// <summary>Gets or sets the connection string. The default is the local single-node replica set.</summary>
    public string ConnectionString { get; set; } = "mongodb://localhost:27017/?replicaSet=rs0&directConnection=true";

    /// <summary>Gets or sets the database name.</summary>
    public string Database { get; set; } = "cheaphours";

    /// <summary>
    /// Gets or sets how long an operation waits for a reachable server before failing. Short, so readiness
    /// checks and charger replies fail fast instead of hanging for the driver's 30 s default.
    /// </summary>
    public TimeSpan ServerSelectionTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
