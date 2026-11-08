using System.Text.RegularExpressions;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// A real worker whose car-cloud provider talks to a WireMock vendor fake (OAuth, start and stop endpoints that
/// record every request, failure injection), with one stored vehicle. Polling is slowed and the command monitor
/// quieted, so a test drives dispatch itself.
/// </summary>
internal sealed class VendorCloudWorker : IAsyncDisposable
{
    public static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    private static readonly Regex CommandPath = new("^/api/v1/vehicles/(?<id>[^/]+)/charging/(?<action>start|stop)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly string _keys;
    private int _requestIds;

    private VendorCloudWorker(WireMockVendorFake vendor, IHost host, IMongoDatabase database, DeviceId vehicle, string keys)
    {
        Vendor = vendor;
        Host = host;
        Database = database;
        Vehicle = vehicle;
        _keys = keys;
    }

    public WireMockVendorFake Vendor { get; }

    public IHost Host { get; }

    public IMongoDatabase Database { get; }

    public DeviceId Vehicle { get; }

    public CommandStore Commands => new(Database);

    public CommandDispatcher Dispatcher => Host.Services.GetRequiredService<CommandDispatcher>();

    /// <summary>The start and stop requests the vendor received, oldest first.</summary>
    public IReadOnlyList<VendorRequest> CommandRequests => [.. Vendor.VendorRequests.Where(r => CommandPath.IsMatch(r.Path))];

    public static async Task<VendorCloudWorker> StartAsync(
        MongoFixture mongo,
        RabbitMqFixture rabbit,
        string owner,
        IReadOnlyDictionary<string, string>? extra = null,
        Action<IServiceCollection>? configure = null,
        string? rabbitConnectionString = null,
        Func<IMongoDatabase, DeviceId, Task>? beforeStart = null)
    {
        var vendor = new WireMockVendorFake(oauth: true);
        var databaseName = MongoFixture.NewDatabaseName(owner);
        var database = mongo.Client.GetDatabase(databaseName);
        await MongoIndexes.EnsureAsync(database, TestContext.Current.CancellationToken);
        var keys = Directory.CreateTempSubdirectory("cheaphours-keys-").FullName;

        var settings = new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = $"worker-{Guid.NewGuid():N}",
            ["Providers:CarCloud:BaseAddress"] = vendor.BaseAddress.ToString(),
            ["Providers:CarCloud:TokenEndpoint"] = vendor.TokenEndpoint.ToString(),
            ["Providers:CarCloud:ClientId"] = WireMockVendorFake.ClientId,
            ["Providers:CarCloud:ClientSecret"] = vendor.ClientSecret,
            ["Providers:CarCloud:PollInterval"] = "01:00:00",
            ["Providers:CarCloud:Accounts:0:Owner"] = Owner.Value,
            ["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_fake",
            ["Providers:CarCloud:Accounts:0:RefreshToken"] = vendor.SeedRefreshToken,
            ["DataProtection:KeysPath"] = keys,
            ["Commands:MonitorInterval"] = "01:00:00",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            settings[key] = value;
        }

        var vehicle = await AddVehicleAsync(database, "veh_fake");
        if (beforeStart is not null)
        {
            await beforeStart(database, vehicle);
        }

        var host = await WorkerHost.StartAsync(mongo, rabbitConnectionString ?? rabbit.ConnectionString, databaseName, RabbitMqFixture.NewPrefix(owner), settings, configure: configure);
        var worker = new VendorCloudWorker(vendor, host, database, vehicle, keys);
        vendor.Map(HttpMethod.Get, "^/api/v1/vehicles$", (_, _) => VendorAnswer.Json(new { vehicles = Array.Empty<object>() }));
        vendor.Map(HttpMethod.Post, CommandPath.ToString(), (_, _) => VendorAnswer.Json(new { requestId = $"req-{Interlocked.Increment(ref worker._requestIds)}" }, 202));
        return worker;
    }

    /// <summary>Stores another car-cloud vehicle of the owner, able to start and stop.</summary>
    public static Task<DeviceId> AddVehicleAsync(IMongoDatabase database, string externalId) =>
        new DeviceStateStore(database).EnsureAsync(
            new Device(DeviceId.New(TimeProvider.System), Owner, CarCloudProvider.Id, externalId, DeviceType.Vehicle, Capabilities.StartStop, new DeviceAttributes(77_000, 11_000)),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

    /// <summary>Stores a command for the vehicle, already <c>Published</c>, with an outbox entry the relay will not reach.</summary>
    public async Task<StoredCommand> PublishedAsync(CommandType type = CommandType.StartCharging, DateTimeOffset? dueAt = null)
    {
        var command = await PendingAsync(type, dueAt, availableAt: DateTimeOffset.UtcNow.AddHours(1));
        var loaded = (await Commands.FindAsync(command.Id, TestContext.Current.CancellationToken))!;
        loaded.Command.Publish(DateTimeOffset.UtcNow);
        return (await Commands.SaveAsync(loaded, TestContext.Current.CancellationToken))!;
    }

    /// <summary>Stores a <c>Pending</c> command for the vehicle with its outbox entry.</summary>
    public async Task<StoredCommand> PendingAsync(CommandType type = CommandType.StartCharging, DateTimeOffset? dueAt = null, DateTimeOffset? availableAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), Vehicle, type, $"key_{Guid.NewGuid():N}"), now, dueAt ?? now),
            Owner,
            null);
        Assert.True(await Commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now, availableAt), TestContext.Current.CancellationToken));
        return command;
    }

    public async Task<StoredCommand> ReadAsync(CommandId id) => (await Commands.FindAsync(id, TestContext.Current.CancellationToken))!;

    public async ValueTask DisposeAsync()
    {
        await WorkerHost.StopAsync(Host);
        Vendor.Dispose();
        Directory.Delete(_keys, recursive: true);
    }
}
