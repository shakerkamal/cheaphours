using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Closes this replica's charger connections that have been silent for more than three heartbeat intervals.
/// Checks every quarter interval on the injected clock. The connection's exit path then marks the device
/// offline and removes its registry entry.
/// </summary>
/// <param name="connections">The open connections.</param>
/// <param name="time">The clock.</param>
/// <param name="options">The OCPP options (heartbeat interval).</param>
/// <param name="logger">The logger.</param>
internal sealed partial class HeartbeatMonitor(ConnectionTable connections, TimeProvider time, IOptions<OcppOptions> options, ILogger<HeartbeatMonitor> logger) : BackgroundService
{
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;

    /// <summary>Gets how often connections are checked.</summary>
    public TimeSpan CheckPeriod => _interval / 4;

    /// <summary>Aborts every connection that is no longer online at <paramref name="now"/>.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>How many connections were aborted.</returns>
    public int Sweep(DateTimeOffset now)
    {
        var aborted = 0;
        foreach (var connection in connections.Snapshot())
        {
            if (!DevicePresence.IsOnline(connection.LastSeenAt, _interval, now))
            {
                LogSilent(connection.ChargePointId, now - connection.LastSeenAt);

                // A half-open TCP connection may never finish a close handshake, so don't wait for one.
                connection.Abort(CloseReason.HeartbeatTimeout);
                aborted++;
            }
        }

        return aborted;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckPeriod, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Sweep(time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Charger {ChargePointId} silent for {Silence}; closing its connection and marking it offline")]
    private partial void LogSilent(string chargePointId, TimeSpan silence);
}
