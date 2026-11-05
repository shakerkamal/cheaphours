using CheapHours.Persistence.Locks;
using MongoDB.Driver;

namespace CheapHours.Host.Goals;

/// <summary>
/// Runs a short scheduled job on one worker replica (design §6): take the named lease, run the job with a
/// deadline of half the lease, release the lease in <c>finally</c>. A replica that cannot take the lease skips
/// the run. The runs are short, so unlike polling they need no renewal loop; the deadline keeps a run from
/// outliving its lease.
/// </summary>
internal static class LeasedRun
{
    /// <summary>Runs <paramref name="job"/> if <paramref name="holder"/> can take <paramref name="lease"/>.</summary>
    /// <param name="lease">The lease.</param>
    /// <param name="holder">This replica's id.</param>
    /// <param name="leaseDuration">How long the lease is taken for; the job gets half of it.</param>
    /// <param name="time">The clock for the deadline.</param>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns><see langword="true"/> when the job ran; <see langword="false"/> when another replica holds the lease.</returns>
    public static async Task<bool> RunAsync(
        ILease lease,
        string holder,
        TimeSpan leaseDuration,
        TimeProvider time,
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(job);
        if (!await lease.TryAcquireAsync(holder, leaseDuration, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            using var deadline = new CancellationTokenSource(leaseDuration / 2, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            await job(linked.Token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try
            {
                await lease.ReleaseAsync(holder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // The database is unreachable: the lease expires on its own, and the next run waits for that.
            }
        }
    }
}
