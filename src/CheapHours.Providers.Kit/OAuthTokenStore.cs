using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Access tokens for one OAuth provider's accounts (design §2). Tokens are cached decrypted in memory and
/// stored protected through <see cref="IProviderAccountRepository"/>. Per account:
/// <list type="bullet">
/// <item>an access token is refreshed 60 s before expiry or after a 401, once at a time (single flight);</item>
/// <item>a refreshed (possibly rotated) token is stored, compare-and-set on the version, before it is used;</item>
/// <item><c>invalid_grant</c> re-reads the store first: a version that moved means another replica rotated, not a revocation;</item>
/// <item>a revocation or unreadable tokens mark the account reconnect-needed and are logged by provider and account only.</item>
/// </list>
/// No token or client secret is ever logged, thrown or put in a failure reason.
/// </summary>
internal sealed partial class OAuthTokenStore
{
    /// <summary>How long before expiry an access token is refreshed.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    /// <summary>Assumed lifetime when the token endpoint gives no <c>expires_in</c>.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Provider, string Account), Entry> _entries = new();
    private readonly OAuthProviderOptions _options;
    private readonly string _tokenClientName;
    private readonly IHttpClientFactory _clients;
    private readonly IProviderAccountRepository _accounts;
    private readonly TokenProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<OAuthTokenStore> _logger;

    public OAuthTokenStore(
        OAuthProviderOptions options,
        string tokenClientName,
        IHttpClientFactory clients,
        IProviderAccountRepository accounts,
        TokenProtector protector,
        TimeProvider time,
        ILogger<OAuthTokenStore> logger)
    {
        _options = options;
        _tokenClientName = tokenClientName;
        _clients = clients;
        _accounts = accounts;
        _protector = protector;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a usable access token, refreshing it when it is missing or about to expire.</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or the failure that prevents one.</returns>
    public async Task<TokenLease> GetAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        var entry = EntryFor(account);
        if (Usable(entry.Current) is { } cached)
        {
            return cached;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Usable(entry.Current) is { } refreshedMeanwhile)
            {
                return refreshedMeanwhile;
            }

            if (entry.Current is null)
            {
                var failure = await LoadAsync(entry, account, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return TokenLease.Failed(failure);
                }

                if (Usable(entry.Current) is { } loaded)
                {
                    return loaded;
                }
            }

            return await RefreshLockedAsync(entry, account, attemptsLeft: 2, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Gets a new access token after the vendor refused the one of <paramref name="rejectedVersion"/>.</summary>
    /// <param name="account">The account.</param>
    /// <param name="rejectedVersion">The version of the refused token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A newer token (another caller's refresh, or a new one), or the failure.</returns>
    public async Task<TokenLease> RefreshAfterRejectionAsync(ProviderAccount account, long rejectedVersion, CancellationToken cancellationToken)
    {
        var entry = EntryFor(account);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Current is { } current && current.Version > rejectedVersion && Usable(current) is { } newer)
            {
                return newer;
            }

            if (entry.Current is null)
            {
                var failure = await LoadAsync(entry, account, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return TokenLease.Failed(failure);
                }
            }

            return await RefreshLockedAsync(entry, account, attemptsLeft: 2, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Marks the account reconnect-needed (for example after a fresh token was refused too) and drops its cached tokens.</summary>
    /// <param name="account">The account.</param>
    /// <param name="reason">A fixed, secret-free reason for the log.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The reconnect-needed failure to return to the caller.</returns>
    public async Task<VendorFailure> MarkReconnectNeededAsync(ProviderAccount account, string reason, CancellationToken cancellationToken)
    {
        var entry = EntryFor(account);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MarkLockedAsync(entry, account, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private Entry EntryFor(ProviderAccount account) => _entries.GetOrAdd((account.ProviderId, account.ExternalAccountId), _ => new Entry());

    private TokenLease? Usable(Snapshot? snapshot) =>
        snapshot is not null && snapshot.Tokens.IsUsable(_time.GetUtcNow(), RefreshMargin)
            ? new TokenLease(snapshot.Tokens.AccessToken, snapshot.Version, null)
            : null;

    /// <summary>Reads the stored tokens into the entry.</summary>
    /// <returns><see langword="null"/> when loaded; otherwise the failure.</returns>
    private async Task<VendorFailure?> LoadAsync(Entry entry, ProviderAccount account, CancellationToken cancellationToken)
    {
        entry.Current = null;
        var stored = await _accounts.ReadTokensAsync(account, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.ReconnectNeeded)
        {
            return ReconnectNeeded();
        }

        if (!_protector.TryUnprotect(stored.ProtectedTokens, out var tokens))
        {
            LogUnreadable(account.ProviderId, account.ExternalAccountId);
            return await MarkLockedAsync(entry, account, "stored tokens unreadable", cancellationToken).ConfigureAwait(false);
        }

        entry.Current = new Snapshot(tokens!, stored.Version);
        return null;
    }

    private async Task<TokenLease> RefreshLockedAsync(Entry entry, ProviderAccount account, int attemptsLeft, CancellationToken cancellationToken)
    {
        var current = entry.Current!;
        var outcome = await RequestAsync(current.Tokens.RefreshToken, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case Grant grant:
                var tokens = new OAuthTokens(grant.AccessToken, _time.GetUtcNow() + grant.Lifetime, grant.RefreshToken ?? current.Tokens.RefreshToken);
                if (await _accounts.TryWriteTokensAsync(account, _protector.Protect(tokens), current.Version, cancellationToken).ConfigureAwait(false))
                {
                    entry.Current = new Snapshot(tokens, current.Version + 1);
                    LogRefreshed(account.ProviderId, account.ExternalAccountId);
                    return new TokenLease(tokens.AccessToken, current.Version + 1, null);
                }

                // Another writer stored first: drop this result and use what is stored.
                return await AfterConflictAsync(entry, account, attemptsLeft, cancellationToken).ConfigureAwait(false);

            case InvalidGrant:
                var failure = await LoadAsync(entry, account, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return TokenLease.Failed(failure);
                }

                if (entry.Current!.Version != current.Version)
                {
                    // Another replica rotated the refresh token first: not a revocation.
                    return Usable(entry.Current) ?? (attemptsLeft > 1
                        ? await RefreshLockedAsync(entry, account, attemptsLeft - 1, cancellationToken).ConfigureAwait(false)
                        : TokenLease.Failed(new VendorFailure(VendorFailureKind.Transient, "token-refresh-conflict")));
                }

                return TokenLease.Failed(await MarkLockedAsync(entry, account, "refresh token revoked (invalid_grant)", cancellationToken).ConfigureAwait(false));

            case Refused refused:
                return TokenLease.Failed(refused.Failure);

            default:
                throw new InvalidOperationException("Unknown token endpoint outcome.");
        }
    }

    private async Task<TokenLease> AfterConflictAsync(Entry entry, ProviderAccount account, int attemptsLeft, CancellationToken cancellationToken)
    {
        var failure = await LoadAsync(entry, account, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return TokenLease.Failed(failure);
        }

        return Usable(entry.Current) ?? (attemptsLeft > 1
            ? await RefreshLockedAsync(entry, account, attemptsLeft - 1, cancellationToken).ConfigureAwait(false)
            : TokenLease.Failed(new VendorFailure(VendorFailureKind.Transient, "token-refresh-conflict")));
    }

    private async Task<VendorFailure> MarkLockedAsync(Entry entry, ProviderAccount account, string reason, CancellationToken cancellationToken)
    {
        entry.Current = null;
        await _accounts.MarkReconnectNeededAsync(account, cancellationToken).ConfigureAwait(false);
        LogReconnectNeeded(account.ProviderId, account.ExternalAccountId, reason);
        return ReconnectNeeded();
    }

    private static VendorFailure ReconnectNeeded() => new(VendorFailureKind.ReconnectNeeded, VendorFailure.ReconnectNeededReason);

    /// <summary>Calls the token endpoint with the refresh-token grant (client_secret_post).</summary>
    private async Task<TokenOutcome> RequestAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", _options.ClientId!),
                new("client_secret", _options.ClientSecret!),
            ]),
        };

        try
        {
            using var response = await _clients.CreateClient(_tokenClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ReadGrant(body);
            }

            var error = VendorFailureClassifier.ErrorCode(body);
            if (response.StatusCode == HttpStatusCode.BadRequest && error == "invalid_grant")
            {
                return new InvalidGrant();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new Refused(new VendorFailure(VendorFailureKind.Rejected, "token endpoint refused the client" + (error is null ? string.Empty : $" ({error})")));
            }

            return new Refused(await VendorFailureClassifier.ClassifyAsync(response, _time, cancellationToken).ConfigureAwait(false)
                ?? new VendorFailure(VendorFailureKind.Transient, "token endpoint answered unexpectedly"));
        }
        catch (Exception ex) when (VendorFailureClassifier.Classify(ex, cancellationToken) is { } failure)
        {
            return new Refused(failure);
        }
    }

    private static TokenOutcome ReadGrant(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var access) && access.ValueKind == JsonValueKind.String && access.GetString() is { Length: > 0 } accessToken)
            {
                var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : DefaultLifetime;
                var refreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String && refresh.GetString() is { Length: > 0 } r
                    ? r
                    : null;
                return new Grant(accessToken, lifetime, refreshToken);
            }
        }
        catch (JsonException)
        {
        }

        return new Refused(new VendorFailure(VendorFailureKind.Transient, "token endpoint answer unreadable"));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {ProviderId} account {ExternalAccountId} needs to be linked again: {Reason}")]
    private partial void LogReconnectNeeded(string providerId, string externalAccountId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored tokens of provider {ProviderId} account {ExternalAccountId} cannot be decrypted (key ring replaced?)")]
    private partial void LogUnreadable(string providerId, string externalAccountId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refreshed the access token of provider {ProviderId} account {ExternalAccountId}")]
    private partial void LogRefreshed(string providerId, string externalAccountId);

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Snapshot? Current { get; set; }
    }

    private sealed record Snapshot(OAuthTokens Tokens, long Version);

    private abstract record TokenOutcome;

    private sealed record Grant(string AccessToken, TimeSpan Lifetime, string? RefreshToken) : TokenOutcome
    {
        public override string ToString() => "Grant";
    }

    private sealed record InvalidGrant : TokenOutcome;

    private sealed record Refused(VendorFailure Failure) : TokenOutcome;
}
