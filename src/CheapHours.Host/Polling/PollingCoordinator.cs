using System.Security.Cryptography;
using CheapHours.Host.Worker;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.ProviderAccounts;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Polling;

/// <summary>
/// Makes exactly one worker replica poll (design §5). After the worker's startup it tries to acquire the polling
/// lease; while it holds it, it runs one <see cref="ProviderPollingLoop"/> per polling provider and renews the
/// lease. It stops the loops when the lease is taken by another replica or would expire before a renew could
/// succeed. On shutdown it stops the loops, waits for the polls in flight and releases the lease, so a standby
/// takes over within one acquire interval.
/// </summary>
internal sealed partial class PollingCoordinator(
    [FromKeyedServices(LeaseNames.DevicePolling)] ILease lease,
    IEnumerable<IPollingProvider> providers,
    IEnumerable<ProviderPollingSettings> settings,
    IPollingSchedule schedule,
    IPollRecorder recorder,
    WorkerState state,
    IOptions<PollingOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _logger = loggers.CreateLogger<PollingCoordinator>();
    private readonly PollingOptions _options = options.Value;
    private readonly string _holder = configuration["Worker:ReplicaId"] is { Length: > 0 } id
        ? id
        : $"{Environment.MachineName}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";

    /// <summary>Gets this replica's lease holder id.</summary>
    public string Holder => _holder;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var polling = providers.ToList();
        if (polling.Count == 0)
        {
            LogNoProviders();
            return;
        }

        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            var enabled = string.Join(", ", polling.Select(p => p.ProviderId));
            LogEnabled(enabled, _holder);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await TryAcquireAsync(stoppingToken).ConfigureAwait(false))
                {
                    LogLeader(_holder);
                    await LeadAsync(polling, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(_options.AcquireInterval, time, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await lease.TryAcquireAsync(_holder, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            LogLeaseUnreachable(ex.GetType().Name);
            return false;
        }
    }

    private async Task LeadAsync(List<IPollingProvider> polling, CancellationToken stoppingToken)
    {
        using var leading = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var loops = polling.Select(provider => new ProviderPollingLoop(
                provider,
                SettingsFor(provider),
                schedule,
                recorder,
                _options,
                time,
                loggers.CreateLogger($"{typeof(ProviderPollingLoop).FullName}.{provider.ProviderId}")))
            .Select(loop => loop.RunAsync(leading.Token))
            .ToList();

        var renewedAt = time.GetUtcNow();
        var shuttingDown = false;
        try
        {
            while (true)
            {
                await Task.Delay(_options.RenewInterval, time, stoppingToken).ConfigureAwait(false);
                bool? renewed;
                try
                {
                    renewed = await lease.TryAcquireAsync(_holder, _options.LeaseDuration, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is MongoException or TimeoutException)
                {
                    LogLeaseUnreachable(ex.GetType().Name);
                    renewed = null;
                }

                if (renewed == true)
                {
                    renewedAt = time.GetUtcNow();
                    continue;
                }

                // Taken by another replica, or unreachable for so long that the lease would lapse before the next renew.
                if (renewed == false || time.GetUtcNow() + _options.RenewInterval >= renewedAt + _options.LeaseDuration)
                {
                    LogLeaseLost(_holder);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            shuttingDown = true;
        }

        await leading.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(loops).ConfigureAwait(false);

        if (shuttingDown)
        {
            try
            {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await lease.ReleaseAsync(_holder, release.Token).ConfigureAwait(false);
                LogReleased(_holder);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
            {
                LogLeaseUnreachable(ex.GetType().Name);
            }
        }
    }

    // The last registration wins, as usual in DI.
    private ProviderPollingSettings SettingsFor(IPollingProvider provider) =>
        settings.LastOrDefault(s => s.ProviderType == provider.GetType())
        ?? new ProviderPollingSettings(provider.GetType(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));

    [LoggerMessage(Level = LogLevel.Information, Message = "No polling providers are registered; this worker does not poll")]
    private partial void LogNoProviders();

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling providers enabled: {Providers}; replica {Holder} competes for the polling lease")]
    private partial void LogEnabled(string providers, string holder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replica {Holder} holds the polling lease and polls")]
    private partial void LogLeader(string holder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replica {Holder} lost the polling lease and stopped polling")]
    private partial void LogLeaseLost(string holder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replica {Holder} released the polling lease")]
    private partial void LogReleased(string holder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The polling lease could not be reached ({Error})")]
    private partial void LogLeaseUnreachable(string error);
}
