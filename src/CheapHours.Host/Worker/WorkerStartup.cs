using CheapHours.Messaging;
using CheapHours.Persistence;
using CheapHours.Providers.Kit;
using MongoDB.Driver;

namespace CheapHours.Host.Worker;

/// <summary>
/// Prepares MongoDB (collections and indexes, then the configured provider accounts) and RabbitMQ (connection
/// and topology), retrying while either is unreachable. A collection or queue of the wrong shape, or a write the
/// database refuses (such as an owner linked twice to one provider), is not retried: it stops the worker.
/// </summary>
internal sealed partial class WorkerStartup(
    IMongoDatabase database,
    RabbitMqConnection broker,
    MessagingTopology topology,
    WorkerState state,
    TimeProvider time,
    ILogger<WorkerStartup> logger,
    ProviderAccountSeeder? seeder = null) : BackgroundService
{
    /// <summary>The pause between attempts while a dependency is unreachable.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MongoIndexes.EnsureAsync(database, stoppingToken).ConfigureAwait(false);
                if (seeder is not null)
                {
                    await seeder.SeedAsync(stoppingToken).ConfigureAwait(false);
                }

                var connection = await broker.ConnectAsync(stoppingToken).ConfigureAwait(false);
                await TopologyDeclarer.DeclareAsync(connection, topology, stoppingToken).ConfigureAwait(false);

                state.MarkReady();
                LogReady(broker.Endpoint);
                return;
            }
            catch (Exception ex) when (ex is (MongoException and not MongoWriteException) or TimeoutException or BrokerUnavailableException)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker initialised: indexes ensured, provider accounts seeded, topology declared on {Endpoint}")]
    private partial void LogReady(string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dependency is not reachable yet ({Error}); retrying in {Delay}")]
    private partial void LogRetry(string error, TimeSpan delay);
}
