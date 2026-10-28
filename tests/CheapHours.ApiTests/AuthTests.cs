using System.Net;
using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CheapHours.ApiTests;

public sealed class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri Devices = new("/v1/devices", UriKind.Relative);
    private static readonly Uri Confirm = new("/v1/plans/plan_1/confirm", UriKind.Relative);

    // A read endpoint that is not built yet, so passing auth shows as 501 without a database.
    private static readonly Uri Savings = new("/v1/savings?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z", UriKind.Relative);

    [Fact]
    public async Task Missing_key_gets_401()
    {
        using var client = factory.CreateClient(apiKey: null);

        using var response = await client.GetAsync(Devices, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains("X-Api-Key", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_key_gets_401_and_is_never_echoed()
    {
        const string unknown = "ch_live_this_key_does_not_exist_0000000000";
        using var client = factory.CreateClient(unknown);

        using var response = await client.GetAsync(Devices, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.DoesNotContain(unknown, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_key_on_an_execute_endpoint_gets_403()
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.PostAsync(Confirm, content: null, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Execute_key_passes_both_scopes()
    {
        using var client = factory.CreateClient(ApiFactory.ExecuteKey);

        using var read = await client.GetAsync(Savings, TestContext.Current.CancellationToken);
        using var execute = await client.PostAsync(Confirm, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, execute.StatusCode);
    }

    [Fact]
    public void Keys_are_stored_as_sha256_hashes()
    {
        var hash = ApiKeyHasher.Hash("ch_live_example");

        Assert.True(ApiKeyHasher.IsHash(hash));
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(hash, ApiKeyHasher.Hash("ch_live_example2"));
    }

    [Fact]
    public void Every_key_must_hold_the_read_scope()
    {
        var hash = ApiKeyHasher.Hash("k");

        Assert.Throws<ArgumentException>(() => new ApiKeyRecord("exec-only", hash, OwnerId.Parse("own_demo"), [ApiScopes.Execute]));
        Assert.Throws<ArgumentException>(() => new ApiKeyRecord("admin", hash, OwnerId.Parse("own_demo"), [ApiScopes.Read, "admin"]));
        Assert.Throws<ArgumentException>(() => new ApiKeyRecord("plaintext", "ch_live_not_a_hash", OwnerId.Parse("own_demo"), [ApiScopes.Read]));
    }

    [Fact]
    public void Invalid_configured_key_stops_startup_naming_the_key_and_the_scope()
    {
        var hash = ApiKeyHasher.Hash("ch_test_admin_key");
        using var broken = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:2:Name"] = "too-powerful",
            ["ApiKeys:Keys:2:Hash"] = hash,
            ["ApiKeys:Keys:2:Owner"] = "own_test",
            ["ApiKeys:Keys:2:Scopes:0"] = "read",
            ["ApiKeys:Keys:2:Scopes:1"] = "admin",
        })));

        var error = Assert.Throws<OptionsValidationException>(() => broken.CreateClient());

        Assert.Contains("too-powerful", error.Message, StringComparison.Ordinal);
        Assert.Contains("admin", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, error.Message, StringComparison.Ordinal);
    }
}
