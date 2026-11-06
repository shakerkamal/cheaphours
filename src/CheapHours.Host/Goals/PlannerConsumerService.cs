using CheapHours.Host.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Goals;

/// <summary>Runs the planner consumer on <c>worker.planner</c> once the worker's startup has finished.</summary>
internal sealed class PlannerConsumerService(
    RabbitMqConnection broker,
    RabbitMqPublisher publisher,
    MessagingTopology topology,
    PricesPublishedHandler handler,
    IOptions<MessagingOptions> options,
    WorkerState state,
    TimeProvider time,
    ILogger<PlannerConsumerService> logger) : BackgroundService
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

        var consumer = new BatchingConsumer<PricesPublished>(
            broker, publisher, topology.Planner, handler, PricesPublished.CurrentSchemaVersion, options, time, logger);
        await consumer.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
