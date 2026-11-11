using System.Collections.Concurrent;

namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>
/// Every <see cref="SimulatorOptions.Interval"/> on the injected clock, delivers a <c>device.state</c> event for every
/// heat pump. Each delivery runs on its own, so a receiver that is down (and the retries it causes) never delays the
/// next interval. Deliveries still running at shutdown are cancelled.
/// </summary>
internal sealed class DeliveryService(SimulatorOptions options, SimulatorState state, WebhookSender sender, TimeProvider time) : BackgroundService
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private PeriodicTimer? _timer;

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // ExecuteAsync runs on the thread pool, so a timer created there could miss a clock that moves right after
        // start; created here, the schedule starts when the host starts.
        _timer = new PeriodicTimer(options.Interval, time);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = _timer ?? throw new InvalidOperationException("The delivery service runs only after StartAsync.");
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var webhookEvent in state.StateEvents())
                {
                    var delivery = Task.Run(() => sender.DeliverAsync(webhookEvent, stoppingToken), stoppingToken);
                    _running[delivery] = 0;
                    _ = delivery.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        try
        {
            await Task.WhenAll(_running.Keys).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
