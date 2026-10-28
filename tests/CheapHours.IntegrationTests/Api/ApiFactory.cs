using System.Net;
using System.Security.Cryptography;
using System.Text;
using CheapHours.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// Runs the host in the API role against the MongoDB fixture, with its own database and three configured
/// keys: a read key and an execute key for <c>own_a</c>, and a read key for <c>own_b</c>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>A key of <c>own_a</c> with the read scope.</summary>
    public const string ReadKeyA = "ch_it_read_key_of_owner_a_for_tests";

    /// <summary>A key of <c>own_a</c> with the read and execute scopes.</summary>
    public const string ExecuteKeyA = "ch_it_execute_key_of_owner_a_for_tests";

    /// <summary>A key of <c>own_b</c> with the read scope.</summary>
    public const string ReadKeyB = "ch_it_read_key_of_owner_b_for_tests";

    private readonly string _connectionString;
    private readonly TimeProvider? _time;
    private readonly IReadOnlyDictionary<string, string?> _overrides;

    /// <summary>Creates the host; call <see cref="StartAsync"/> to start it.</summary>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="time">A clock to use instead of the system clock.</param>
    /// <param name="overrides">Extra or replaced configuration values.</param>
    public ApiFactory(string connectionString, TimeProvider? time = null, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        _connectionString = connectionString;
        _time = time;
        _overrides = overrides ?? new Dictionary<string, string?>();
        DatabaseName = MongoFixture.NewDatabaseName("api");
    }

    /// <summary>Gets the database this host uses.</summary>
    public string DatabaseName { get; init; }

    /// <summary>Gets every log line the host wrote.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The SHA-256 of a key, as stored.</summary>
    /// <param name="key">The key.</param>
    /// <returns>64 lower-case hex characters.</returns>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

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

    /// <summary>Starts the host and, unless told not to, waits until it reports ready (keys seeded).</summary>
    /// <param name="waitUntilReady">Whether to wait for <c>/health/ready</c> to answer 200.</param>
    /// <returns>A task that completes when started.</returns>
    public async Task StartAsync(bool waitUntilReady = true)
    {
        using var client = CreateClient();
        if (!waitUntilReady)
        {
            return;
        }

        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The API did not become ready.");
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("role", "api");
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mongo"] = _connectionString,
                ["Mongo:Database"] = DatabaseName,
            };
            AddKey(values, 0, "a-read", ReadKeyA, "own_a", "read");
            AddKey(values, 1, "a-execute", ExecuteKeyA, "own_a", "read", "execute");
            AddKey(values, 2, "b-read", ReadKeyB, "own_b", "read");
            foreach (var (key, value) in _overrides)
            {
                values[key] = value;
            }

            config.AddInMemoryCollection(values);
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));

        if (_time is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_time);
            });
        }
    }

    private static void AddKey(Dictionary<string, string?> values, int index, string name, string key, string owner, params string[] scopes)
    {
        values[$"ApiKeys:Keys:{index}:Name"] = name;
        values[$"ApiKeys:Keys:{index}:Hash"] = Hash(key);
        values[$"ApiKeys:Keys:{index}:Owner"] = owner;
        for (var i = 0; i < scopes.Length; i++)
        {
            values[$"ApiKeys:Keys:{index}:Scopes:{i}"] = scopes[i];
        }
    }
}
