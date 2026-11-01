using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;

namespace CheapHours.Providers.Kit.Testing;

/// <summary>
/// A faithful in-memory <see cref="IProviderAccountRepository"/>: the same seed rules and version
/// compare-and-set as the MongoDB store, for unit and contract tests.
/// </summary>
public sealed class InMemoryProviderAccountRepository : IProviderAccountRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Provider, string Account), Row> _rows = [];

    /// <summary>Gets how many token writes succeeded.</summary>
    public int Writes { get; private set; }

    /// <summary>Called after every successful token write, with the account; for ordering checks.</summary>
    public Action<ProviderAccount>? OnWritten { get; set; }

    /// <inheritdoc/>
    public Task<SeedOutcome> SeedAsync(ProviderAccount account, string protectedTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_lock)
        {
            var key = Key(account);
            if (!_rows.TryGetValue(key, out var row))
            {
                _rows[key] = new Row(account.OwnerId, protectedTokens, 1, ReconnectNeeded: false);
                return Task.FromResult(SeedOutcome.Inserted);
            }

            if (row.ReconnectNeeded)
            {
                _rows[key] = row with { ProtectedTokens = protectedTokens, Version = row.Version + 1, ReconnectNeeded = false };
                return Task.FromResult(SeedOutcome.Relinked);
            }

            return Task.FromResult(SeedOutcome.Unchanged);
        }
    }

    /// <inheritdoc/>
    public Task<StoredAccountTokens?> ReadTokensAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_lock)
        {
            return Task.FromResult(_rows.TryGetValue(Key(account), out var row)
                ? new StoredAccountTokens(row.ProtectedTokens, row.Version, row.ReconnectNeeded)
                : null);
        }
    }

    /// <inheritdoc/>
    public Task<bool> TryWriteTokensAsync(ProviderAccount account, string protectedTokens, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_lock)
        {
            var key = Key(account);
            if (!_rows.TryGetValue(key, out var row) || row.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            _rows[key] = row with { ProtectedTokens = protectedTokens, Version = row.Version + 1 };
            Writes++;
        }

        OnWritten?.Invoke(account);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task MarkReconnectNeededAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_lock)
        {
            if (_rows.TryGetValue(Key(account), out var row))
            {
                _rows[Key(account)] = row with { ReconnectNeeded = true };
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Returns the stored row as it is, or <see langword="null"/>.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The stored tokens.</returns>
    public StoredAccountTokens? Peek(ProviderAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_lock)
        {
            return _rows.TryGetValue(Key(account), out var row) ? new StoredAccountTokens(row.ProtectedTokens, row.Version, row.ReconnectNeeded) : null;
        }
    }

    private static (string, string) Key(ProviderAccount account) => (account.ProviderId, account.ExternalAccountId);

    private sealed record Row(OwnerId Owner, string ProtectedTokens, long Version, bool ReconnectNeeded);
}
