using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using static CheapHours.UnitTests.Kit.OAuthHarness;

namespace CheapHours.UnitTests.Kit;

/// <summary>Seeding accounts from configuration without overwriting rotated tokens (design §2).</summary>
public sealed class ProviderAccountSeederTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_account_is_inserted_with_the_seed_token()
    {
        var harness = new OAuthHarness();

        await using var sp = await harness.StartReplicaAsync();

        var stored = harness.Accounts.Peek(Account);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
        Assert.False(stored.ReconnectNeeded);
        Assert.DoesNotContain(FakeTokenEndpoint.SeedRefreshToken, stored.ProtectedTokens, StringComparison.Ordinal);
        Assert.True(harness.Logs.Contains("Inserted"));
    }

    [Fact]
    public async Task Restart_after_rotation_keeps_the_rotated_token()
    {
        var harness = new OAuthHarness();
        await using (var first = await harness.StartReplicaAsync())
        {
            using var _ = await first.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        }

        var rotated = harness.Accounts.Peek(Account)!;
        await using var restarted = await harness.StartReplicaAsync();

        Assert.Equal(rotated, harness.Accounts.Peek(Account));
        harness.Time.Advance(TimeSpan.FromMinutes(20));
        using var response = await restarted.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal("rt-1-secret", harness.TokenEndpoint.RefreshTokensUsed.Last());
    }

    [Fact]
    public async Task Relink_after_revocation_uses_the_new_seed_token()
    {
        var harness = new OAuthHarness();
        await using (var first = await harness.StartReplicaAsync())
        {
            harness.TokenEndpoint.Revoke();
            using var _ = await first.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        }

        Assert.True(harness.Accounts.Peek(Account)!.ReconnectNeeded);

        // The operator links the account again: the vendor issues a new seed, configured before the restart.
        var fresh = new OAuthHarness { Accounts = harness.Accounts, KeyRing = harness.KeyRing, Time = harness.Time, Logs = harness.Logs };
        await using var restarted = await fresh.StartReplicaAsync();
        using var response = await restarted.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        Assert.False(harness.Accounts.Peek(Account)!.ReconnectNeeded);
        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal([FakeTokenEndpoint.SeedRefreshToken], fresh.TokenEndpoint.RefreshTokensUsed);
        Assert.True(harness.Logs.Contains("Relinked"));
    }
}
