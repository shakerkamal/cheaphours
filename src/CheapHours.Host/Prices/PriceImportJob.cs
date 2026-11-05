using System.Globalization;
using CheapHours.Host.Devices;
using CheapHours.Host.Goals;
using CheapHours.Host.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.Prices;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Prices;

/// <summary>
/// Imports day-ahead prices (design §8). After the worker's startup it runs at once and then on
/// <see cref="PriceImportSchedule"/>. Each run holds the <c>price-import</c> lease, so one replica calls the
/// source: it fetches today until complete and, from the first-attempt time to the give-up time, tomorrow until
/// complete, at most two requests a run. A complete day is announced once with <see cref="PricesPublished"/>,
/// whose id comes from the zone and the day, so a run that could not reach the broker is followed by the same
/// message on the next run. A failing run is logged with its outcome only and never stops the worker.
/// </summary>
internal sealed partial class PriceImportJob(
    [FromKeyedServices(LeaseNames.PriceImport)] ILease lease,
    IPriceSource source,
    IPriceStore prices,
    IPriceDayStore days,
    IMessagePublisher publisher,
    WorkerState state,
    IOptions<PricesOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<PriceImportJob> logger) : BackgroundService
{
    private readonly PricesOptions _options = options.Value;
    private readonly PriceImportSchedule _schedule = new(options.Value);
    private readonly string _holder = ReplicaIds.From(configuration);

    /// <summary>Returns the id of the message that announces <paramref name="zone"/>'s prices for <paramref name="day"/>.</summary>
    /// <param name="zone">The bidding zone.</param>
    /// <param name="day">The Berlin day.</param>
    /// <returns>The same id every time.</returns>
    public static Guid AnnouncementId(string zone, DateOnly day) =>
        DeterministicGuid.From(string.Create(CultureInfo.InvariantCulture, $"prices\n{zone}\n{day:yyyy-MM-dd}"));

    /// <summary>Runs one import if this replica can take the lease.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns><see langword="true"/> when this replica ran the import.</returns>
    public Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        LeasedRun.RunAsync(lease, _holder, _options.LeaseDuration, time, ImportAsync, cancellationToken);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogRunFailed(ex.GetType().Name);
                }

                var now = time.GetUtcNow();
                await Task.Delay(_schedule.Next(now) - now, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var zone = _options.Zone;
        var today = PriceImportSchedule.Today(now);
        var tomorrow = PriceImportSchedule.Tomorrow(now);
        var requested = false;

        if (await days.GetAsync(zone, today, cancellationToken).ConfigureAwait(false) is not { IsComplete: true })
        {
            requested = true;
            if (!await ImportDayAsync(zone, today, now, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        var tomorrowState = await days.GetAsync(zone, tomorrow, cancellationToken).ConfigureAwait(false);
        if (tomorrowState is not { IsComplete: true } && tomorrowState?.GaveUpAt is null)
        {
            if (_schedule.FetchesTomorrow(now))
            {
                if (requested && _options.PauseBetweenRequests > TimeSpan.Zero)
                {
                    await Task.Delay(_options.PauseBetweenRequests, time, cancellationToken).ConfigureAwait(false);
                }

                if (!await ImportDayAsync(zone, tomorrow, now, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            else if (_schedule.PastGiveUp(now))
            {
                // A worker that started after the give-up time has not tried tomorrow yet: try once first.
                if (tomorrowState?.AttemptedAt is null)
                {
                    if (requested && _options.PauseBetweenRequests > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.PauseBetweenRequests, time, cancellationToken).ConfigureAwait(false);
                    }

                    if (!await ImportDayAsync(zone, tomorrow, now, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    tomorrowState = await days.GetAsync(zone, tomorrow, cancellationToken).ConfigureAwait(false);
                }

                if (tomorrowState is not { IsComplete: true }
                    && await days.MarkGaveUpAsync(zone, tomorrow, BerlinTime.SlotCount(tomorrow), now, cancellationToken).ConfigureAwait(false))
                {
                    LogGaveUp(zone, tomorrow);
                }
            }
        }

        await AnnounceAsync(zone, today, cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(zone, tomorrow, cancellationToken).ConfigureAwait(false);
    }

    // Fetches and stores one day. Returns false when the run must end (the source rate-limited us).
    private async Task<bool> ImportDayAsync(string zone, DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await days.MarkAttemptedAsync(zone, day, BerlinTime.SlotCount(day), now, cancellationToken).ConfigureAwait(false);
        switch (await source.FetchAsync(zone, day, cancellationToken).ConfigureAwait(false))
        {
            case PriceFetch.Published published:
                if (published.Deprecated)
                {
                    LogDeprecated(zone);
                }

                var written = await prices.UpsertAsync(published.Day.Slots, now, cancellationToken).ConfigureAwait(false);
                var stored = await prices.CountAsync(zone, BerlinTime.DayStartUtc(day), BerlinTime.DayEndUtc(day), cancellationToken).ConfigureAwait(false);
                var slotCount = published.Day.SlotCount;
                await days.RecordImportAsync(zone, day, slotCount, stored >= slotCount, now, cancellationToken).ConfigureAwait(false);
                LogImported(zone, day, stored, slotCount, written.Inserted, written.Modified);
                return true;
            case PriceFetch.NotPublished:
                LogNotPublished(zone, day);
                return true;
            case PriceFetch.RateLimited:
                LogRateLimited(zone, day);
                return false;
            case PriceFetch.Rejected rejected:
                LogRejected(zone, day, rejected.Rule);
                return true;
            case PriceFetch.Failed failed:
                LogFetchFailed(zone, day, failed.Reason);
                return true;
            default:
                throw new InvalidOperationException("Unknown price fetch outcome.");
        }
    }

    private async Task AnnounceAsync(string zone, DateOnly day, CancellationToken cancellationToken)
    {
        if (await days.GetAsync(zone, day, cancellationToken).ConfigureAwait(false) is not { IsComplete: true, AnnouncedAt: null } complete)
        {
            return;
        }

        var message = new PricesPublished(AnnouncementId(zone, day), time.GetUtcNow(), traceparent: null, zone, day, complete.SlotCount);
        try
        {
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            LogAnnouncementDeferred(zone, day);
            return;
        }

        await days.MarkAnnouncedAsync(zone, day, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        LogAnnounced(zone, day, complete.SlotCount);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Prices for {Zone} on {Day}: {Stored} of {SlotCount} slots stored ({Inserted} new, {Modified} changed)")]
    private partial void LogImported(string zone, DateOnly day, int stored, int slotCount, int inserted, int modified);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prices for {Zone} on {Day} are not published yet")]
    private partial void LogNotPublished(string zone, DateOnly day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price source rate-limited the request for {Zone} on {Day}; the run ends and the next run tries again")]
    private partial void LogRateLimited(string zone, DateOnly day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prices for {Zone} on {Day} rejected (rule {Rule}); nothing stored, the next run tries again")]
    private partial void LogRejected(string zone, DateOnly day, PriceRule rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price fetch for {Zone} on {Day} failed ({Reason}); the next run tries again")]
    private partial void LogFetchFailed(string zone, DateOnly day, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The price source marks its endpoint deprecated (zone {Zone})")]
    private partial void LogDeprecated(string zone);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up on prices for {Zone} on {Day}: not complete by the give-up time; plans use the slots that have prices")]
    private partial void LogGaveUp(string zone, DateOnly day);

    [LoggerMessage(Level = LogLevel.Information, Message = "Announced prices for {Zone} on {Day} ({SlotCount} slots)")]
    private partial void LogAnnounced(string zone, DateOnly day, int slotCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not announce prices for {Zone} on {Day}: broker unavailable; the next run announces them")]
    private partial void LogAnnouncementDeferred(string zone, DateOnly day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price import run failed ({ExceptionType}); the next run tries again")]
    private partial void LogRunFailed(string exceptionType);
}
