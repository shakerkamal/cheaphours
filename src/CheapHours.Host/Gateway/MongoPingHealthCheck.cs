using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CheapHours.Host.Gateway;

/// <summary>Healthy when MongoDB answers a <c>ping</c> within two seconds. Never reports why it failed.</summary>
/// <param name="database">The database.</param>
internal sealed class MongoPingHealthCheck(IMongoDatabase database) : IHealthCheck
{
    /// <summary>How long the ping may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly BsonDocument Ping = new("ping", 1);

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await database.RunCommandAsync<BsonDocument>(Ping, cancellationToken: timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("MongoDB did not answer.");
        }
    }
}
