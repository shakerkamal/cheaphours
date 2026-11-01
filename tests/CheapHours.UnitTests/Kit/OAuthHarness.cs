using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Kit;

/// <summary>A token endpoint with the refresh-token grant: rotates refresh tokens, can revoke, counts requests.</summary>
internal sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public const string ClientSecret = "client-secret-7f3a";
    public const string SeedRefreshToken = "seed-rt-91c2";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _validRefresh = [SeedRefreshToken];
    private readonly HashSet<string> _issuedAccess = [];
    private int _issued;

    public ConcurrentQueue<string> Events { get; init; } = new();

    public ConcurrentQueue<string> RefreshTokensUsed { get; } = new();

    public int Count => RefreshTokensUsed.Count;

    /// <summary>Gets or sets a gate the endpoint waits on before answering, to hold refreshes in flight.</summary>
    public Task Hold { get; set; } = Task.CompletedTask;

    public bool IsIssued(string accessToken)
    {
        lock (_lock)
        {
            return _issuedAccess.Contains(accessToken);
        }
    }

    public IReadOnlyList<string> AllSecrets()
    {
        lock (_lock)
        {
            return [ClientSecret, SeedRefreshToken, .. Enumerable.Range(1, _issued).SelectMany(n => new[] { $"at-{n}-secret", $"rt-{n}-secret" })];
        }
    }

    public void Revoke()
    {
        lock (_lock)
        {
            _validRefresh.Clear();
            _issuedAccess.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&')
            .Select(p => p.Split('='))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        RefreshTokensUsed.Enqueue(form["refresh_token"]);
        await Hold.WaitAsync(cancellationToken);

        if (form["client_secret"] != ClientSecret)
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        }

        lock (_lock)
        {
            if (form["grant_type"] != "refresh_token" || !_validRefresh.Remove(form["refresh_token"]))
            {
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }

            var n = ++_issued;
            var access = $"at-{n}-secret";
            var refresh = $"rt-{n}-secret";
            _issuedAccess.Add(access);
            _validRefresh.Add(refresh);
            Events.Enqueue($"issued:{access}");
            return Json(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","token_type":"Bearer","expires_in":900,"refresh_token":"{{refresh}}"}""");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A vendor API that accepts only access tokens the endpoint issued (and not refused on purpose).</summary>
internal sealed class FakeOAuthVendor(FakeTokenEndpoint endpoint) : HttpMessageHandler
{
    public ConcurrentQueue<string?> BearerTokens { get; } = new();

    public HashSet<string> Refuse { get; } = [];

    public int Count => BearerTokens.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
        BearerTokens.Enqueue(token);
        endpoint.Events.Enqueue($"vendor:{token}");
        var ok = token is not null && endpoint.IsIssued(token) && !Refuse.Contains(token);
        return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"echo":"client_secret=client-secret-7f3a"}""", Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class TestOAuthOptions : OAuthProviderOptions;

internal sealed class OAuthProvider(ProviderHttpClient<OAuthProvider> client) : IPollingProvider
{
    public string ProviderId => "oauthcloud";

    public Capabilities Capabilities => Capabilities.ReadTelemetry;

    public ProviderHttpClient<OAuthProvider> Client { get; } = client;

    public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        Task.FromResult<PollResult>(new PollResult.Completed([]));
}

/// <summary>Shared parts for one or more "replicas": the store, the key ring, the clock, the fakes and the logs.</summary>
internal sealed class OAuthHarness
{
    public static readonly ProviderAccount Account = new("oauthcloud", OwnerId.Parse("own_demo"), "acct_1");

    private FakeOAuthVendor? _vendor;

    public InMemoryProviderAccountRepository Accounts { get; init; } = new();

    public IDataProtectionProvider KeyRing { get; init; } = new EphemeralDataProtectionProvider();

    public FakeTimeProvider Time { get; init; } = new(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero));

    public FakeTokenEndpoint TokenEndpoint { get; init; } = new();

    public CapturingLoggerProvider Logs { get; init; } = new();

    public FakeOAuthVendor Vendor => _vendor ??= new FakeOAuthVendor(TokenEndpoint);

    public static HttpRequestMessage Get() => new(HttpMethod.Get, "api/v1/vehicles");

    /// <summary>Builds a service provider ("replica") on the shared parts and seeds the configured account.</summary>
    /// <param name="keyRing">A different key ring for this replica, if any.</param>
    /// <param name="seedRefreshToken">The configured seed refresh token, if not the default.</param>
    /// <returns>The replica's service provider.</returns>
    public async Task<ServiceProvider> StartReplicaAsync(IDataProtectionProvider? keyRing = null, string? seedRefreshToken = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Providers:OAuth:BaseAddress"] = "https://vendor.example/",
            ["Providers:OAuth:TokenEndpoint"] = "https://vendor.example/oauth/token",
            ["Providers:OAuth:ClientId"] = "client-1",
            ["Providers:OAuth:ClientSecret"] = FakeTokenEndpoint.ClientSecret,
            ["Providers:OAuth:RateLimit:RequestsPerMinute"] = "600",
            ["Providers:OAuth:Http:MaxRetries"] = "0",
            ["Providers:OAuth:Http:MinimumThroughput"] = "1000",
            ["Providers:OAuth:Accounts:0:Owner"] = "own_demo",
            ["Providers:OAuth:Accounts:0:ExternalAccountId"] = Account.ExternalAccountId,
            ["Providers:OAuth:Accounts:0:RefreshToken"] = seedRefreshToken ?? FakeTokenEndpoint.SeedRefreshToken,
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IProviderAccountRepository>(Accounts);
        services.AddSingleton(keyRing ?? KeyRing);
        services.AddProvider<OAuthProvider, TestOAuthOptions>("Providers:OAuth");
        var vendor = Vendor;
        var endpoint = TokenEndpoint;
        services.AddHttpClient(typeof(OAuthProvider).FullName!).ConfigurePrimaryHttpMessageHandler(() => vendor);
        services.AddHttpClient(typeof(OAuthProvider).FullName + ".token").ConfigurePrimaryHttpMessageHandler(() => endpoint);

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<ProviderAccountSeeder>().SeedAsync(TestContext.Current.CancellationToken);
        return sp;
    }
}
