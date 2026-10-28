using CheapHours.Host.Api.Auth;
using CheapHours.Persistence;
using CheapHours.Persistence.ApiKeys;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Api;

/// <summary>
/// Creates the indexes and seeds the configured API keys into <c>apiKeys</c>, retrying until MongoDB
/// answers, then marks the API ready. Until then readiness fails, so a slow database delays the API
/// instead of crash-looping it. The configured keys were already validated at startup.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="keys">The stored keys.</param>
/// <param name="options">The configured keys.</param>
/// <param name="state">The API state to mark.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class ApiStartup(
    IMongoDatabase database,
    IStoredApiKeys keys,
    IOptions<ApiKeyOptions> options,
    ApiState state,
    TimeProvider time,
    ILogger<ApiStartup> logger) : BackgroundService
{
    /// <summary>The pause between attempts while MongoDB is unreachable.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MongoIndexes.EnsureAsync(database, stoppingToken).ConfigureAwait(false);
                foreach (var entry in options.Value.Keys)
                {
                    // Validated by ApiKeyOptionsValidator before the host started, so this always succeeds.
                    if (ApiKeyOptionsValidator.TryCreate(entry, out var record, out _))
                    {
                        var stored = new StoredApiKey(record.KeyHash, record.Name, record.OwnerId.Value, [.. record.Scopes.Order(StringComparer.Ordinal)]);
                        await keys.SeedAsync(stored, time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                    }
                }

                state.MarkReady();
                LogReady(options.Value.Keys.Count);
                return;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                LogRetry(ex.GetType().Name, RetryDelay);
            }

            try
            {
                await Task.Delay(RetryDelay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API initialised: indexes ensured, {Count} configured API key(s) seeded")]
    private partial void LogReady(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MongoDB is not reachable yet ({Error}); retrying in {Delay}")]
    private partial void LogRetry(string error, TimeSpan delay);
}
