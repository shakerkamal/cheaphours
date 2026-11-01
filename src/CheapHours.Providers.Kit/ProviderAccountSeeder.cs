using CheapHours.Core.Ids;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Seeds every configured account of every OAuth provider registration (design §2): a missing account is
/// inserted with its protected seed refresh token and polled at once; an account marked reconnect-needed is
/// linked again with the seed; any other account keeps its stored, possibly rotated, tokens. The worker runs
/// it at startup, after the indexes exist. Registered only when an OAuth provider is.
/// </summary>
public sealed partial class ProviderAccountSeeder
{
    private readonly IEnumerable<OAuthRegistration> _registrations;
    private readonly IServiceProvider _services;
    private readonly IProviderAccountRepository _accounts;
    private readonly TokenProtector _protector;
    private readonly ILogger<ProviderAccountSeeder> _logger;

    internal ProviderAccountSeeder(
        IEnumerable<OAuthRegistration> registrations,
        IServiceProvider services,
        IProviderAccountRepository accounts,
        TokenProtector protector,
        ILogger<ProviderAccountSeeder> logger)
    {
        _registrations = registrations;
        _services = services;
        _accounts = accounts;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Seeds every configured account.</summary>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <returns>A task that completes when every account is seeded.</returns>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _registrations)
        {
            var provider = (IDeviceProvider)_services.GetRequiredService(registration.ProviderType);
            foreach (var configured in registration.Options(_services).Accounts)
            {
                var account = new ProviderAccount(provider.ProviderId, OwnerId.Parse(configured.Owner!), configured.ExternalAccountId!);
                var seed = _protector.Protect(new OAuthTokens(AccessToken: null, AccessExpiresAt: null, configured.RefreshToken!));
                var outcome = await _accounts.SeedAsync(account, seed, cancellationToken).ConfigureAwait(false);
                LogSeeded(account.ProviderId, account.ExternalAccountId, outcome);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderId} account {ExternalAccountId} seeded: {Outcome}")]
    private partial void LogSeeded(string providerId, string externalAccountId, SeedOutcome outcome);
}
