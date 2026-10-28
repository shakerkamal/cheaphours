using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Worker;

/// <summary>Runs the telemetry consumer once the worker's startup has finished.</summary>
internal sealed class TelemetryConsumerService(
    RabbitMqConnection broker,
    RabbitMqPublisher publisher,
    MessagingTopology topology,
    TelemetryBatchHandler handler,
    IOptions<MessagingOptions> options,
    WorkerState state,
    TimeProvider time,
    ILogger<TelemetryConsumerService> logger,
    TelemetryConsumerHooks? hooks = null) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var consumer = new BatchingConsumer<TelemetryReceived>(
            broker, publisher, topology.Telemetry, handler, TelemetryReceived.CurrentSchemaVersion, options, time, logger)
        {
            AfterHandledForTests = hooks?.AfterHandled,
        };

        await consumer.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
