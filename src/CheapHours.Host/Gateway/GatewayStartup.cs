using CheapHours.Core.Ids;
using CheapHours.Persistence;
using CheapHours.Persistence.ChargePoints;
using CheapHours.Providers.Ocpp;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Creates the indexes and seeds the configured charger registrations, retrying until MongoDB answers, then
/// marks the gateway initialised. Until then readiness fails and chargers get 503, so a slow database at
/// startup delays the gateway instead of crash-looping it. Invalid configuration still fails startup, because
/// the options are validated before this runs.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="registrations">The registrations store.</param>
/// <param name="options">The OCPP options with the configured registrations.</param>
/// <param name="state">The gateway state to mark.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class GatewayStartup(
    IMongoDatabase database,
    IChargePointRegistrations registrations,
    IOptions<OcppOptions> options,
    GatewayState state,
    TimeProvider time,
    ILogger<GatewayStartup> logger) : BackgroundService
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
                foreach (var cp in options.Value.ChargePoints)
                {
                    var registration = new ChargePointRegistration(cp.Id, OwnerId.Parse(cp.OwnerId), cp.PasswordHash.ToLowerInvariant(), cp.MaxPowerW);
                    await registrations.SeedAsync(registration, time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }

                state.MarkInitialised();
                LogReady(options.Value.ChargePoints.Count);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Gateway initialised: indexes ensured, {Count} charger registration(s) seeded")]
    private partial void LogReady(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MongoDB is not reachable yet ({Error}); retrying in {Delay}")]
    private partial void LogRetry(string error, TimeSpan delay);
}
