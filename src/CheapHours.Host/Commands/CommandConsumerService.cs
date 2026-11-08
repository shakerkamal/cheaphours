using CheapHours.Host.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Commands;

/// <summary>
/// Runs a command consumer (the dispatcher on <c>worker.dispatch</c>, or the gateway results on
/// <c>worker.command-results</c>) once the worker's startup has finished. Handler failures get the usual delay
/// queues and dead-letter queue.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
internal sealed class CommandConsumerService<TMessage>(
    RabbitMqConnection broker,
    RabbitMqPublisher publisher,
    MessagingTopology topology,
    IBatchHandler<TMessage> handler,
    IOptions<MessagingOptions> options,
    WorkerState state,
    TimeProvider time,
    ILogger<CommandConsumerService<TMessage>> logger) : BackgroundService
    where TMessage : Message
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

        var (queues, schemaVersion) = typeof(TMessage) == typeof(CommandRequested)
            ? (topology.Dispatch, CommandRequested.CurrentSchemaVersion)
            : (topology.CommandResults, GatewayCommandResult.CurrentSchemaVersion);
        var consumer = new BatchingConsumer<TMessage>(broker, publisher, queues, handler, schemaVersion, options, time, logger);
        await consumer.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
