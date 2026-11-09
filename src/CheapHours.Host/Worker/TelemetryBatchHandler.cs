using System.Diagnostics;
using CheapHours.Host.Observability;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Telemetry;
using MongoDB.Driver;

namespace CheapHours.Host.Worker;

/// <summary>
/// Stores delivered <see cref="TelemetryReceived"/> messages. MongoDB being unreachable is reported as
/// <see cref="DependencyUnavailableException"/>, so the consumer waits it out instead of spending retries;
/// any other failure is the message's and goes through the normal retries. Each batch is one span that links to
/// the trace of every message in it (a batch mixes many devices' traces, so it has none of its own).
/// </summary>
internal sealed class TelemetryBatchHandler(TelemetryBatchWriter writer, TelemetryMetrics metrics) : IBatchHandler<TelemetryReceived>
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

        using var span = SpanScope.StartRoot(CheapHoursDiagnostics.Telemetry, "telemetry store", ActivityKind.Internal, Links(batch))
            .Tag("cheaphours.message.count", batch.Count);
        try
        {
            var stored = await writer.WriteAsync(ConsumerName, items, cancellationToken).ConfigureAwait(false);
            span.Tag("cheaphours.reading.count", stored.Values.Sum());
            metrics.Stored(stored);
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            span.Fail(ex);
            throw new DependencyUnavailableException("MongoDB is not reachable.", ex);
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            throw;
        }
    }

    // One link per distinct trace context a message of the batch carries.
    private static List<ActivityLink> Links(IReadOnlyList<Delivered<TelemetryReceived>> batch)
    {
        var links = new List<ActivityLink>();
        foreach (var traceparent in batch.Select(d => d.Message.Traceparent).Distinct(StringComparer.Ordinal))
        {
            if (SpanScope.TryParse(traceparent, out var context))
            {
                links.Add(new ActivityLink(context));
            }
        }

        return links;
    }

    private static bool IsOutage(Exception ex) =>
        ex is TimeoutException or MongoConnectionException or MongoNotPrimaryException or MongoNodeIsRecoveringException;
}
