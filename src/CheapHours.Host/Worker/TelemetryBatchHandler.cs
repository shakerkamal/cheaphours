using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Telemetry;
using MongoDB.Driver;

namespace CheapHours.Host.Worker;

/// <summary>
/// Stores delivered <see cref="TelemetryReceived"/> messages. MongoDB being unreachable is reported as
/// <see cref="DependencyUnavailableException"/>, so the consumer waits it out instead of spending retries;
/// any other failure is the message's and goes through the normal retries.
/// </summary>
internal sealed class TelemetryBatchHandler(TelemetryBatchWriter writer) : IBatchHandler<TelemetryReceived>
{
    /// <summary>The inbox scope of this consumer.</summary>
    public const string ConsumerName = "worker.telemetry";

    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<Delivered<TelemetryReceived>> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var items = batch
            .Select(d => new TelemetryBatchItem(d.Message.MessageId, d.Message.DeviceId, d.Message.ProviderId, d.Message.Readings, d.IsRepeat))
            .ToList();

        try
        {
            await writer.WriteAsync(ConsumerName, items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            throw new DependencyUnavailableException("MongoDB is not reachable.", ex);
        }
    }

    private static bool IsOutage(Exception ex) =>
        ex is TimeoutException or MongoConnectionException or MongoNotPrimaryException or MongoNodeIsRecoveringException;
}
