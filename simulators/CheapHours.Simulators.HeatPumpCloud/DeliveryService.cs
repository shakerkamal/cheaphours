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

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, time);
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
