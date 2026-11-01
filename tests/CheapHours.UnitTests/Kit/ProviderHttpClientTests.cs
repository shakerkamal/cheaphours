using System.Net;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static CheapHours.UnitTests.Kit.HttpKitHarness;

namespace CheapHours.UnitTests.Kit;

/// <summary>The per-provider HTTP pipeline (design §1) through <see cref="ProviderHttpClient{TProvider}"/>.</summary>
public sealed class ProviderHttpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Retry_on_a_transient_error()
    {
        var vendor = StubVendor.Sequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await using var sp = Build(vendor);

        using var response = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal(2, vendor.Count);
    }

    [Fact]
    public async Task No_retry_on_429()
    {
        var vendor = new StubVendor((_, _) =>
        {
            var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            tooMany.Headers.TryAddWithoutValidation("Retry-After", "30");
            return tooMany;
        });
        await using var sp = Build(vendor);

        using var response = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        var failed = Assert.IsType<VendorResponse.Failed>(response);
        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "vendor-rate-limited", TimeSpan.FromSeconds(30)), failed.Failure);
        Assert.Equal(1, vendor.Count);
    }

    [Fact]
    public async Task Other_client_errors_are_not_retried()
    {
        var vendor = StubVendor.Always(HttpStatusCode.BadRequest);
        await using var sp = Build(vendor);

        using var response = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        Assert.Equal(VendorFailureKind.Rejected, Assert.IsType<VendorResponse.Failed>(response).Failure.Kind);
        Assert.Equal(1, vendor.Count);
    }

    [Fact]
    public async Task Retries_are_bounded_and_end_transient()
    {
        var vendor = StubVendor.Always(HttpStatusCode.InternalServerError);
        await using var sp = Build(vendor);

        using var response = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "vendor returned 500"), Assert.IsType<VendorResponse.Failed>(response).Failure);
        Assert.Equal(3, vendor.Count); // one attempt and the default two retries
    }

    [Fact]
    public async Task Breakers_are_per_provider()
    {
        var vendorA = StubVendor.Always(HttpStatusCode.InternalServerError);
        var vendorB = StubVendor.Always(HttpStatusCode.OK);
        await using var sp = Build(vendorA, vendorB, OpensAfterTwoFailures);
        var a = sp.GetRequiredService<HttpProviderA>().Client;

        for (var i = 0; i < 3; i++)
        {
            using var _ = await a.SendAsync(AccountA, Get, Ct);
        }

        using var b = await sp.GetRequiredService<HttpProviderB>().Client.SendAsync(AccountB, Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(b);
        Assert.Equal(1, vendorB.Count);
    }

    [Fact]
    public async Task Open_circuit_sends_nothing_and_is_circuit_open_with_the_break_duration()
    {
        var vendor = StubVendor.Always(HttpStatusCode.InternalServerError);
        await using var sp = Build(vendor, settings: OpensAfterTwoFailures);
        var client = sp.GetRequiredService<HttpProviderA>().Client;
        using (await client.SendAsync(AccountA, Get, Ct))
        using (await client.SendAsync(AccountA, Get, Ct))
        {
        }

        var sentBefore = vendor.Count;
        using var response = await client.SendAsync(AccountA, Get, Ct);

        var failed = Assert.IsType<VendorResponse.Failed>(response);
        Assert.Equal(new VendorFailure(VendorFailureKind.Transient, "circuit-open", TimeSpan.FromSeconds(30)), failed.Failure);
        Assert.Equal(sentBefore, vendor.Count);
    }

    [Fact]
    public async Task Connection_failure_is_retried_then_transient()
    {
        var vendor = new StubVendor((_, _) => throw new HttpRequestException("connection refused"));
        await using var sp = Build(vendor);

        using var response = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        Assert.Equal("vendor-unreachable", Assert.IsType<VendorResponse.Failed>(response).Failure.Reason);
        Assert.Equal(3, vendor.Count);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        await using var sp = Build(StubVendor.Always(HttpStatusCode.OK));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, cancelled.Token));
    }

    [Fact]
    public async Task Requests_go_to_the_configured_base_address()
    {
        var vendor = StubVendor.Always(HttpStatusCode.OK);
        await using var sp = Build(vendor);

        using var _ = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(AccountA, Get, Ct);

        Assert.Equal(new Uri("https://vendor.example/api/v1/vehicles"), vendor.Requests.Single().RequestUri);
    }

    [Fact]
    public async Task Polling_settings_are_recorded_per_provider()
    {
        await using var sp = Build(StubVendor.Always(HttpStatusCode.OK), StubVendor.Always(HttpStatusCode.OK), s => s["Providers:B:PollInterval"] = "00:00:15");

        var settings = sp.GetServices<ProviderPollingSettings>().ToDictionary(s => s.ProviderType);

        Assert.Equal(TimeSpan.FromSeconds(60), settings[typeof(HttpProviderA)].PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), settings[typeof(HttpProviderB)].PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings[typeof(HttpProviderA)].TotalTimeout);
    }

    [Fact]
    public async Task Invalid_options_name_the_key_without_the_value()
    {
        await using var sp = Build(StubVendor.Always(HttpStatusCode.OK), settings: s =>
        {
            s["Providers:A:BaseAddress"] = "not-absolute-secretish";
            s["Providers:A:PollInterval"] = "00:00:02";
        });

        var error = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<TestHttpOptionsA>>().Value);

        Assert.Contains("Providers:A:BaseAddress", error.Message, StringComparison.Ordinal);
        Assert.Contains("Providers:A:PollInterval", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secretish", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bearer_token_is_attached()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();

        using var response = await sp.GetRequiredService<OAuthProvider>().Client.SendAsync(OAuthHarness.Account, OAuthHarness.Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal(["at-1-secret"], harness.Vendor.BearerTokens);
    }

    [Fact]
    public async Task Unauthorized_refreshes_once_and_retries_once()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        var client = sp.GetRequiredService<OAuthProvider>().Client;
        using (await client.SendAsync(OAuthHarness.Account, OAuthHarness.Get, Ct))
        {
        }

        harness.Vendor.Refuse.Add("at-1-secret");
        using var response = await client.SendAsync(OAuthHarness.Account, OAuthHarness.Get, Ct);

        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal(["at-1-secret", "at-1-secret", "at-2-secret"], harness.Vendor.BearerTokens);
        Assert.Equal(2, harness.TokenEndpoint.Count);
    }

    [Fact]
    public async Task A_second_unauthorized_is_reconnect_needed()
    {
        var harness = new OAuthHarness();
        await using var sp = await harness.StartReplicaAsync();
        var client = sp.GetRequiredService<OAuthProvider>().Client;
        harness.Vendor.Refuse.UnionWith(["at-1-secret", "at-2-secret"]);

        using var response = await client.SendAsync(OAuthHarness.Account, OAuthHarness.Get, Ct);

        Assert.Equal(VendorFailureKind.ReconnectNeeded, Assert.IsType<VendorResponse.Failed>(response).Failure.Kind);
        Assert.Equal(2, harness.Vendor.Count);
        Assert.Equal(2, harness.TokenEndpoint.Count);
        Assert.True(harness.Accounts.Peek(OAuthHarness.Account)!.ReconnectNeeded);
    }

    private static void OpensAfterTwoFailures(Dictionary<string, string?> settings)
    {
        settings["Providers:A:Http:MaxRetries"] = "0";
        settings["Providers:A:Http:MinimumThroughput"] = "2";
        settings["Providers:A:Http:FailureRatio"] = "0.5";
    }
}
