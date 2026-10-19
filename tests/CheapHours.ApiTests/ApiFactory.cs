using CheapHours.Host.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CheapHours.ApiTests;

/// <summary>Runs the host in the given role with two test API keys (stored as hashes, like real ones).</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>A key holding only the read scope.</summary>
    public const string ReadKey = "ch_test_read_key_for_api_tests_only";

    /// <summary>A key holding the read and execute scopes.</summary>
    public const string ExecuteKey = "ch_test_execute_key_for_api_tests_only";

    /// <summary>Gets the role the host runs in.</summary>
    protected virtual string Role => "api";

    /// <summary>Creates a client that sends <paramref name="apiKey"/>, or no key when it is <see langword="null"/>.</summary>
    /// <param name="apiKey">The API key to send.</param>
    /// <returns>The client.</returns>
    public HttpClient CreateClient(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("role", Role);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:0:Name"] = "test-read",
            ["ApiKeys:Keys:0:Hash"] = ApiKeyHasher.Hash(ReadKey),
            ["ApiKeys:Keys:0:Owner"] = "own_test",
            ["ApiKeys:Keys:0:Scopes:0"] = "read",
            ["ApiKeys:Keys:1:Name"] = "test-execute",
            ["ApiKeys:Keys:1:Hash"] = ApiKeyHasher.Hash(ExecuteKey),
            ["ApiKeys:Keys:1:Owner"] = "own_test",
            ["ApiKeys:Keys:1:Scopes:0"] = "read",
            ["ApiKeys:Keys:1:Scopes:1"] = "execute",
        }));
    }
}

/// <summary>Runs the host in the gateway role.</summary>
public sealed class GatewayFactory : ApiFactory
{
    /// <inheritdoc/>
    protected override string Role => "gateway";
}
