using System.Net;
using System.Security.Cryptography;
using System.Text;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Persistence.ChargePoints;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>
/// Runs the real host in the gateway role on Kestrel (a real port), against a real MongoDB database of its
/// own. Two chargers are registered: <c>CP001</c> and <c>CP002</c>.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    /// <summary>The replica id the host runs as.</summary>
    public const string ReplicaId = "gw-test";

    /// <summary>CP001's password (40 hex characters, like a generated one).</summary>
    public const string Password = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>CP002's password.</summary>
    public const string OtherPassword = "fedcba9876543210fedcba9876543210fedcba98";

    private readonly string _connectionString;
    private readonly TimeProvider? _time;
    private readonly IReadOnlyDictionary<string, string?> _overrides;

    /// <summary>Creates the host; call <see cref="StartAsync"/> to start it.</summary>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="time">A clock to use instead of the system clock.</param>
    /// <param name="overrides">Extra or replaced configuration values.</param>
    public GatewayFactory(string connectionString, TimeProvider? time = null, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        _connectionString = connectionString;
        _time = time;
        _overrides = overrides ?? new Dictionary<string, string?>();
        DatabaseName = MongoFixture.NewDatabaseName("gateway");
        UseKestrel(0);
    }

    /// <summary>Gets the database this host uses.</summary>
    public string DatabaseName { get; init; }

    /// <summary>Gets the prefix of every exchange and queue this host declares, so hosts never share them.</summary>
    public string QueuePrefix { get; } = RabbitMqFixture.NewPrefix("gateway");

    /// <summary>Gets the topology the host declared.</summary>
    public MessagingTopology Topology => Services.GetRequiredService<MessagingTopology>();

    /// <summary>Gets every log line the host wrote.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Gets the host's address, once started.</summary>
    public Uri BaseUri { get; private set; } = null!;

    /// <summary>Gets the registrations store the host uses.</summary>
    public IChargePointRegistrations Registrations => Services.GetRequiredService<IChargePointRegistrations>();

    /// <summary>Gets the device store the host uses.</summary>
    public IDeviceStateStore Devices => Services.GetRequiredService<IDeviceStateStore>();

    /// <summary>Gets the connection registry the host uses.</summary>
    public IConnectionRegistry Registry => Services.GetRequiredService<IConnectionRegistry>();

    /// <summary>The SHA-256 of a password, as stored.</summary>
    /// <param name="password">The password.</param>
    /// <returns>64 lower-case hex characters.</returns>
    public static string Hash(string password) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    /// <summary>Starts the server and, unless told not to, waits until it reports ready.</summary>
    /// <param name="waitUntilReady">Whether to wait for <c>/health/ready</c> to answer 200.</param>
    /// <returns>A task that completes when started.</returns>
    public async Task StartAsync(bool waitUntilReady = true)
    {
        StartServer();
        using (var client = CreateClient())
        {
            BaseUri = client.BaseAddress!;
        }

        if (waitUntilReady)
        {
            await WaitUntilReadyAsync();
        }
    }

    /// <summary>The WebSocket address for a charger.</summary>
    /// <param name="chargePointId">The charge point id (already URL-encoded if needed).</param>
    /// <returns>The <c>ws://</c> address.</returns>
    public Uri OcppUri(string chargePointId) => new UriBuilder(BaseUri) { Scheme = "ws", Path = "/ocpp/" + chargePointId }.Uri;

    /// <summary>Polls <c>/health/ready</c> until it answers 200.</summary>
    /// <returns>A task that completes when ready.</returns>
    public async Task WaitUntilReadyAsync()
    {
        using var client = CreateClient();
        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The gateway did not become ready.");
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("role", "gateway");
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mongo"] = _connectionString,
                ["Mongo:Database"] = DatabaseName,
                ["ConnectionStrings:RabbitMq"] = RabbitMqFixture.SharedConnectionString,
                ["Messaging:QueuePrefix"] = QueuePrefix,
                ["Gateway:ReplicaId"] = ReplicaId,
                ["Gateway:DrainTimeoutSeconds"] = "5",
                ["Providers:Ocpp:HeartbeatIntervalSeconds"] = "60",
                ["Providers:Ocpp:ChargePoints:0:Id"] = "CP001",
                ["Providers:Ocpp:ChargePoints:0:OwnerId"] = "own_demo",
                ["Providers:Ocpp:ChargePoints:0:PasswordHash"] = Hash(Password),
                ["Providers:Ocpp:ChargePoints:0:MaxPowerW"] = "11000",
                ["Providers:Ocpp:ChargePoints:1:Id"] = "CP002",
                ["Providers:Ocpp:ChargePoints:1:OwnerId"] = "own_demo",
                ["Providers:Ocpp:ChargePoints:1:PasswordHash"] = Hash(OtherPassword),
                ["Providers:Ocpp:ChargePoints:1:MaxPowerW"] = "7400",
            };
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
}
