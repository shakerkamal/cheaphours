using System.Net.WebSockets;
using CheapHours.Persistence.Connections;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Graceful shutdown. As soon as the host is told to stop (SIGTERM, Ctrl+C), it marks the gateway as
/// draining, closes every charger connection with 1001 (going away) and waits up to the drain timeout for the
/// handshakes, then aborts the rest and removes this replica's registry entries.
/// </summary>
internal sealed partial class GatewayDrainService(
    IHostApplicationLifetime lifetime,
    GatewayState state,
    ConnectionTable connections,
    IConnectionRegistry registry,
    IOptions<GatewayOptions> options,
    ILogger<GatewayDrainService> logger) : IHostedService
{
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _onStopping;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStopping fires before the server stops, while the sockets are still open.
        _onStopping = lifetime.ApplicationStopping.Register(() => _ = DrainAsync());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _onStopping.DisposeAsync().ConfigureAwait(false);
        if (!state.IsDraining)
        {
            // Stopped without ApplicationStopping (should not happen in a real host); drain now.
            _ = DrainAsync();
        }

        await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        try
        {
            state.BeginDraining();
            var open = connections.Snapshot();
            var timeout = options.Value.DrainTimeout;
            LogDraining(open.Count, timeout);

            await Task.WhenAll(open.Select(c => c.CloseAsync(CloseReason.Shutdown, WebSocketCloseStatus.EndpointUnavailable, "The gateway is shutting down.", timeout))).ConfigureAwait(false);

            try
            {
                await Task.WhenAll(open.Select(c => c.Ended)).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                foreach (var connection in open.Where(c => !c.Ended.IsCompleted))
                {
                    connection.Abort(CloseReason.Shutdown);
                }
            }

            try
            {
                await registry.RemoveReplicaAsync(options.Value.ReplicaId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                LogSweepFailed(ex.GetType().Name);
            }
        }
        finally
        {
            _drained.TrySetResult();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down: closing {Count} charger connection(s), waiting up to {Timeout}")]
    private partial void LogDraining(int count, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove this replica's registry entries ({Error}); stale entries time out with their commands")]
    private partial void LogSweepFailed(string error);
}
