namespace CheapHours.Providers.Kit;

/// <summary>A provider whose vendor we pull from: the worker calls it on a schedule, once per linked account.</summary>
public interface IPollingProvider : IDeviceProvider
{
    /// <summary>Fetches the account's current device state.</summary>
    /// <param name="account">The linked account to poll.</param>
    /// <param name="cancellationToken">Cancels the poll, for example on shutdown.</param>
    /// <returns>
    /// <see cref="PollResult.Completed"/> with normalised events (readings in W, Wh, percent and UTC), or
    /// <see cref="PollResult.Failed"/> when the vendor was unreachable or answered 429 or 5xx.
    /// </returns>
    /// <remarks>
    /// Failure contract:
    /// <list type="bullet">
    /// <item>Vendor failures (429, 5xx, timeouts, connection errors) return <see cref="PollResult.Failed"/>, carrying the vendor's <c>Retry-After</c> when given. They never throw.</item>
    /// <item>A device the vendor reports as unknown or deleted (for example a 404) is returned as a <c>DeviceRemoved</c> event, never dropped silently.</item>
    /// <item>A <c>401</c> leads the kit to refresh the token once and retry once; on <c>invalid_grant</c> the account is marked reconnect-needed and the poll fails.</item>
    /// <item>Throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires, or on a programming error.</item>
    /// </list>
    /// </remarks>
    Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken);
}
