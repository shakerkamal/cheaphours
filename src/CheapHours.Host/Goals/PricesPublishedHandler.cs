using CheapHours.Core.Goals;
using CheapHours.Host.Prices;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Goals;
using CheapHours.Planning;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Goals;

/// <summary>
/// Re-plans open goals when a day's prices are published (design §6). For each zone and day in a batch it loads
/// every owner's open goals whose window reaches into that day and re-plans each: a live draft gets a new draft,
/// a confirmed plan a new confirmed version, an expired draft nothing. A repeated message re-plans to the same
/// result and adds nothing, so no inbox is needed. MongoDB being unreachable pauses the consumer.
/// </summary>
internal sealed partial class PricesPublishedHandler(
    IGoalStore goals,
    GoalPlanningService planning,
    IOptions<PricesOptions> pricesOptions,
    IOptions<PlannerOptions> plannerOptions,
    TimeProvider time,
    ILogger<PricesPublishedHandler> logger) : IBatchHandler<PricesPublished>
{
    /// <inheritdoc/>
    public async Task HandleAsync(IReadOnlyList<Delivered<PricesPublished>> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
            foreach (var (zone, day) in batch.Select(d => (d.Message.Zone, d.Message.DeliveryDay)).Distinct())
            {
                if (!string.Equals(zone, pricesOptions.Value.Zone, StringComparison.Ordinal))
                {
                    LogOtherZone(zone);
                    continue;
                }

                // A goal's window reaches into the day when its departure minus the margin is after the day's start.
                var departingAfter = BerlinTime.DayStartUtc(day) + plannerOptions.Value.SafetyMargin;
                var now = time.GetUtcNow();
                var open = await goals.ListOpenAsync(departingAfter > now ? departingAfter : now, cancellationToken).ConfigureAwait(false);
                var added = 0;
                foreach (var goal in open)
                {
                    if (await planning.ReplanAsync(goal, PlanReason.PricesPublished, reportedSocPercent: null, cancellationToken).ConfigureAwait(false) is ReplanOutcome.Added)
                    {
                        added++;
                    }
                }

                LogHandled(zone, day, open.Count, added);
            }
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            throw new DependencyUnavailableException("MongoDB is not reachable.", ex);
        }
    }

    private static bool IsOutage(Exception ex) =>
        ex is TimeoutException or MongoConnectionException or MongoNotPrimaryException or MongoNodeIsRecoveringException;

    [LoggerMessage(Level = LogLevel.Information, Message = "Prices for {Zone} on {Day} published: {Goals} open goals checked, {Added} re-planned")]
    private partial void LogHandled(string zone, DateOnly day, int goals, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prices for zone {Zone} ignored: not the configured zone")]
    private partial void LogOtherZone(string zone);
}
