namespace CheapHours.Persistence.Locks;

/// <summary>A named lease that lets exactly one worker replica do a job at a time (one document in <c>locks</c>).</summary>
public interface ILease
{
    /// <summary>Gets the lease's name, which is its document id.</summary>
    string Name { get; }

    /// <summary>Acquires the lease, or renews it when <paramref name="holder"/> already holds it.</summary>
    /// <param name="holder">This replica's id.</param>
    /// <param name="duration">How long the lease lasts unless renewed.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns><see langword="true"/> when <paramref name="holder"/> holds the lease until now + <paramref name="duration"/>.</returns>
    Task<bool> TryAcquireAsync(string holder, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Releases the lease if <paramref name="holder"/> holds it; otherwise does nothing.</summary>
    /// <param name="holder">This replica's id.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    /// <returns>A task that completes when released.</returns>
    Task ReleaseAsync(string holder, CancellationToken cancellationToken);
}
