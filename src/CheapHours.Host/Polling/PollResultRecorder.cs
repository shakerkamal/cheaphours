using CheapHours.Host.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.Host.Polling;

/// <summary>
/// Records a completed poll through the shared <see cref="DeviceEventIngestor"/>: discovered devices join the polled
/// account's owner with the poll interval as their report interval, and the poll's start is the messages'
/// occurrence time.
/// </summary>
internal sealed class PollResultRecorder(DeviceEventIngestor ingestor, TimeProvider time) : IPollRecorder
{
    /// <inheritdoc/>
    public Task RecordAsync(IPollingProvider provider, ProviderAccount account, TimeSpan pollInterval, PollResult.Completed result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(result);

        return ingestor.IngestAsync(provider.ProviderId, provider.Capabilities, account.OwnerId, pollInterval, result.Events, time.GetUtcNow(), cancellationToken);
    }
}
