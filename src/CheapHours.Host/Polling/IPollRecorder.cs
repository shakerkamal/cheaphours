using CheapHours.Providers.Kit;

namespace CheapHours.Host.Polling;

/// <summary>Records what a completed poll observed (implemented by <see cref="PollResultRecorder"/>).</summary>
internal interface IPollRecorder
{
    /// <summary>Records a completed poll's events.</summary>
    /// <param name="provider">The provider that polled.</param>
    /// <param name="account">The polled account.</param>
    /// <param name="pollInterval">The provider's poll interval, the report interval of discovered devices.</param>
    /// <param name="result">The poll's result.</param>
    /// <param name="cancellationToken">Cancels the recording.</param>
    /// <returns>A task that completes when recorded.</returns>
    Task RecordAsync(IPollingProvider provider, ProviderAccount account, TimeSpan pollInterval, PollResult.Completed result, CancellationToken cancellationToken);
}
