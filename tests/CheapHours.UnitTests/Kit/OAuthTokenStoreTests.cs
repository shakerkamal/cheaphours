using CheapHours.Providers.Kit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using static CheapHours.UnitTests.Kit.OAuthHarness;

namespace CheapHours.UnitTests.Kit;

/// <summary>Encrypted tokens, single-flight refresh, rotation and revocation (design §2).</summary>
public sealed class OAuthTokenStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Five_concurrent_calls_share_one_refresh()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        var client = sp.GetRequiredService<OAuthProvider>().Client;
        var hold = new TaskCompletionSource();
        harness.TokenEndpoint.Hold = hold.Task;

        var calls = Enumerable.Range(0, 5).Select(_ => client.SendAsync(Account, Get, Ct)).ToArray();
        hold.SetResult();
        var responses = await Task.WhenAll(calls);

        Assert.Equal(1, harness.TokenEndpoint.Count);
        Assert.All(responses, r => Assert.IsType<VendorResponse.Succeeded>(r));
        Assert.Equal(Enumerable.Repeat("at-1-secret", 5), harness.Vendor.BearerTokens);
        Array.ForEach(responses, r => r.Dispose());
    }

    [Fact]
    public async Task Rotation_is_stored_before_the_new_token_is_used()
    {
        var harness = new OAuthHarness();
        harness.Accounts.OnWritten = _ => harness.TokenEndpoint.Events.Enqueue("stored");
        await using var sp = await harness.StartReplicaAsync();

        using var _ = await sp.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        Assert.Equal(["issued:at-1-secret", "stored", "vendor:at-1-secret"], harness.TokenEndpoint.Events);
        Assert.Equal(2, harness.Accounts.Peek(Account)!.Version);
    }

    [Fact]
    public async Task Rotation_stored_survives_a_restart()
    {
        var harness = new OAuthHarness();
        await using (var first = await harness.StartReplicaAsync())
        {
            using var _ = await first.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        }

        harness.Time.Advance(TimeSpan.FromMinutes(20)); // the access token (900 s) has expired
        await using var restarted = await harness.StartReplicaAsync();
        using var response = await restarted.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal([FakeTokenEndpoint.SeedRefreshToken, "rt-1-secret"], harness.TokenEndpoint.RefreshTokensUsed);
    }

    [Fact]
    public async Task A_stored_access_token_is_used_without_a_refresh_after_a_restart()
    {
        var harness = new OAuthHarness();
        await using (var first = await harness.StartReplicaAsync())
        {
            using var _ = await first.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        }

        await using var restarted = await harness.StartReplicaAsync();
        using var response = await restarted.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        Assert.Equal(1, harness.TokenEndpoint.Count);
        Assert.Equal(["at-1-secret", "at-1-secret"], harness.Vendor.BearerTokens);
    }

    [Fact]
    public async Task A_token_is_refreshed_shortly_before_it_expires()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        var client = sp.GetRequiredService<OAuthProvider>().Client;
        using (await client.SendAsync(Account, Get, Ct))
        {
        }

        harness.Time.Advance(TimeSpan.FromSeconds(900 - 59)); // within the 60 s margin
        using var _ = await client.SendAsync(Account, Get, Ct);

        Assert.Equal(2, harness.TokenEndpoint.Count);
        Assert.Equal(["at-1-secret", "at-2-secret"], harness.Vendor.BearerTokens);
    }

    [Fact]
    public async Task A_refresh_lost_to_another_replica_is_not_a_revocation()
    {
        var harness = new OAuthHarness();
        await using var replica1 = await harness.StartReplicaAsync();
        await using var replica2 = await harness.StartReplicaAsync();
        var client1 = replica1.GetRequiredService<OAuthProvider>().Client;
        var client2 = replica2.GetRequiredService<OAuthProvider>().Client;
        using (await client1.SendAsync(Account, Get, Ct))
        using (await client2.SendAsync(Account, Get, Ct))
        {
        }

        harness.Time.Advance(TimeSpan.FromMinutes(20));
        using (await client1.SendAsync(Account, Get, Ct))
        {
        }

        using var response = await client2.SendAsync(Account, Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.False(harness.Accounts.Peek(Account)!.ReconnectNeeded);
        Assert.Equal("at-2-secret", harness.Vendor.BearerTokens.Last());
        Assert.Equal([FakeTokenEndpoint.SeedRefreshToken, "rt-1-secret", "rt-1-secret"], harness.TokenEndpoint.RefreshTokensUsed);
    }

    [Fact]
    public async Task A_revoked_refresh_token_marks_reconnect_needed_without_retrying()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        harness.TokenEndpoint.Revoke();
        var client = sp.GetRequiredService<OAuthProvider>().Client;

        using var response = await client.SendAsync(Account, Get, Ct);
        using var again = await client.SendAsync(Account, Get, Ct);

        Assert.Equal(new VendorFailure(VendorFailureKind.ReconnectNeeded, "reconnect-needed"), Assert.IsType<VendorResponse.Failed>(response).Failure);
        Assert.Equal(VendorFailureKind.ReconnectNeeded, Assert.IsType<VendorResponse.Failed>(again).Failure.Kind);
        Assert.True(harness.Accounts.Peek(Account)!.ReconnectNeeded);
        Assert.Equal(1, harness.TokenEndpoint.Count);
        Assert.Equal(0, harness.Vendor.Count);
        Assert.True(harness.Logs.Contains("oauthcloud") && harness.Logs.Contains("acct_1") && harness.Logs.Contains("invalid_grant"));
    }

    [Fact]
    public async Task A_replaced_key_ring_marks_reconnect_needed_and_is_logged_without_tokens()
    {
        var harness = new OAuthHarness();
        await using (var first = await harness.StartReplicaAsync())
        {
            using var _ = await first.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);
        }

        await using var other = await harness.StartReplicaAsync(keyRing: new EphemeralDataProtectionProvider());
        using var response = await other.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        Assert.Equal(VendorFailureKind.ReconnectNeeded, Assert.IsType<VendorResponse.Failed>(response).Failure.Kind);
        Assert.True(harness.Accounts.Peek(Account)!.ReconnectNeeded);
        var warning = Assert.Single(harness.Logs.Lines, l => l.Contains("cannot be decrypted", StringComparison.Ordinal));
        Assert.Contains("oauthcloud", warning, StringComparison.Ordinal);
        Assert.Contains("acct_1", warning, StringComparison.Ordinal);
        AssertNoSecret(harness);
    }

    [Fact]
    public async Task The_stored_blob_holds_no_token_as_text()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();

        using var _ = await sp.GetRequiredService<OAuthProvider>().Client.SendAsync(Account, Get, Ct);

        var stored = harness.Accounts.Peek(Account)!.ProtectedTokens;
        Assert.All(harness.TokenEndpoint.AllSecrets(), secret => Assert.DoesNotContain(secret, stored, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_exceptions_and_results_never_contain_a_token_or_the_client_secret()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        var client = sp.GetRequiredService<OAuthProvider>().Client;
        var reasons = new List<string>();

        async Task CallAsync(ProviderHttpClient<OAuthProvider> on)
        {
            using var response = await on.SendAsync(Account, Get, Ct);
            if (response is VendorResponse.Failed failed)
            {
                reasons.Add(failed.Failure.ToString());
            }

            reasons.Add(response.ToString());
        }

        await CallAsync(client);                               // refresh, then success
        harness.Vendor.Refuse.Add("at-1-secret");
        await CallAsync(client);                               // 401, refresh, retry
        harness.Vendor.Refuse.Add("at-2-secret");
        harness.Vendor.Refuse.Add("at-3-secret");
        await CallAsync(client);                               // 401 twice: reconnect-needed
        await using var relinked = await harness.StartReplicaAsync();
        harness.TokenEndpoint.Revoke();
        await CallAsync(relinked.GetRequiredService<OAuthProvider>().Client); // revoked

        AssertNoSecret(harness);
        Assert.All(harness.TokenEndpoint.AllSecrets(), secret => Assert.All(reasons, r => Assert.DoesNotContain(secret, r, StringComparison.Ordinal)));
    }

    private static void AssertNoSecret(OAuthHarness harness)
    {
        foreach (var secret in harness.TokenEndpoint.AllSecrets())
        {
            Assert.False(harness.Logs.Contains(secret), $"A log line contains a secret: {string.Join('\n', harness.Logs.Lines)}");
        }
    }
}
