namespace CheapHours.Providers.Kit;

/// <summary>
/// Where linked accounts and their encrypted tokens are kept. The kit owns the tokens' meaning and
/// encryption; the store only keeps an opaque protected blob with a version for compare-and-set writes.
/// Persistence implements it on <c>providerAccounts</c>; Kit.Testing has a faithful in-memory version.
/// </summary>
public interface IProviderAccountRepository
{
    /// <summary>
    /// Seeds an account from configuration: inserts it when missing; replaces the tokens of one marked
    /// reconnect-needed (and clears the mark); otherwise leaves it, so a rotated token is never overwritten.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="protectedTokens">The protected seed tokens.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What the seed did.</returns>
    Task<SeedOutcome> SeedAsync(ProviderAccount account, string protectedTokens, CancellationToken cancellationToken);

    /// <summary>Reads the account's protected tokens.</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored tokens, or <see langword="null"/> when the account does not exist.</returns>
    Task<StoredAccountTokens?> ReadTokensAsync(ProviderAccount account, CancellationToken cancellationToken);

    /// <summary>Writes new protected tokens only when the stored version is still <paramref name="expectedVersion"/>, and increments it.</summary>
    /// <param name="account">The account.</param>
    /// <param name="protectedTokens">The new protected tokens.</param>
    /// <param name="expectedVersion">The version the caller read.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when written; <see langword="false"/> when another writer got there first.</returns>
    Task<bool> TryWriteTokensAsync(ProviderAccount account, string protectedTokens, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Marks the account as needing a new link; it is not polled until seeded again.</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the mark is stored.</returns>
    Task MarkReconnectNeededAsync(ProviderAccount account, CancellationToken cancellationToken);
}
