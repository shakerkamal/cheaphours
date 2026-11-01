using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Worker;

/// <summary>
/// A real worker polling the car-cloud simulator (in-process, its own clock) through MongoDB and RabbitMQ: the
/// discovery, telemetry, token and presence scenarios of the <c>device-polling</c> spec.
/// </summary>
public sealed class WorkerPollingTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly string _database = MongoFixture.NewDatabaseName(nameof(WorkerPollingTests));
    private readonly string _prefix = RabbitMqFixture.NewPrefix(nameof(WorkerPollingTests));
    private readonly string _keys = Directory.CreateTempSubdirectory("cheaphours-keys-").FullName;
    private readonly FakeTimeProvider _simulatorClock = new(TimeProvider.System.GetUtcNow());
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<IHost> _workers = [];
    private SimulatorHost? _simulator;

    private IMongoDatabase Database => mongo.Client.GetDatabase(_database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _simulator = await SimulatorHost.StartAsync(_simulatorClock, SimulatorHost.DefaultSettings(longTokens: false));

    public async ValueTask DisposeAsync()
    {
        // A failed assertion skips a test's own stop: stop its workers here so they do not poll on.
        foreach (var worker in _workers)
        {
            try
            {
                await worker.StopAsync(CancellationToken.None);
            }
            catch (ObjectDisposedException)
            {
            }

            worker.Dispose();
        }

        if (_simulator is not null)
        {
            await _simulator.DisposeAsync();
        }

        _logs.Dispose();
        Directory.Delete(_keys, recursive: true);
    }

    private Dictionary<string, string> Settings()
    {
        var settings = WorkerHost.FastPolling($"worker-{Guid.NewGuid():N}");
        settings["Providers:CarCloud:BaseAddress"] = _simulator!.BaseAddress.ToString();
        settings["Providers:CarCloud:TokenEndpoint"] = new Uri(_simulator.BaseAddress, "oauth/token").ToString();
        settings["Providers:CarCloud:ClientId"] = SimulatorHost.ClientId;
        settings["Providers:CarCloud:ClientSecret"] = SimulatorHost.ClientSecret;
        settings["Providers:CarCloud:PollInterval"] = "00:00:05";
        settings["Providers:CarCloud:Accounts:0:Owner"] = "own_demo";
        settings["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_a";
        settings["Providers:CarCloud:Accounts:0:RefreshToken"] = SimulatorHost.SeedA;
        settings["DataProtection:KeysPath"] = _keys;
        return settings;
    }

    private async Task<IHost> StartWorkerAsync()
    {
        var worker = await WorkerHost.StartAsync(mongo, rabbit, _database, _prefix, Settings(), _logs);
        _workers.Add(worker);
        return worker;
    }

    private async Task<IReadOnlyList<Device>> DevicesAsync() => await new DeviceReader(Database).ListAsync(Owner, null, 10, Ct);

    // A poll's writes land one after another (device, interval, status, last seen): wait for all of them.
    private async Task<Device> VehicleAsync(Func<Device, bool>? condition = null) =>
        (await WorkerHost.EventuallyAsync(
            async () => (await DevicesAsync()).SingleOrDefault(),
            d => d?.State is { LastSeenAt: not null, ReportInterval: not null, LastReading: not null } && (condition?.Invoke(d) ?? true),
            TimeSpan.FromSeconds(20)))!;

    private async Task<long> ReadingsAsync(DeviceId device) =>
        await Database.GetCollection<BsonDocument>(MongoIndexes.Telemetry).CountDocumentsAsync(new BsonDocument("meta.deviceId", device.Value), cancellationToken: Ct);

    private Task<BsonDocument> AccountAsync() =>
        Database.GetCollection<BsonDocument>(MongoIndexes.ProviderAccounts).Find(new BsonDocument("_id", "carcloud/acct_a")).SingleAsync(Ct);

    private async Task WaitForAnotherPollAsync()
    {
        var before = (await AccountAsync())["nextPollAt"].ToUniversalTime();
        await WorkerHost.EventuallyAsync(async () => (await AccountAsync())["nextPollAt"].ToUniversalTime(), next => next > before, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task The_first_poll_discovers_the_vehicle_and_the_second_keeps_it()
    {
        var worker = await StartWorkerAsync();

        var vehicle = await VehicleAsync();
        Assert.Equal(DeviceType.Vehicle, vehicle.Type);
        Assert.Equal("carcloud", vehicle.ProviderId);
        Assert.Equal("veh_a", vehicle.ExternalId);
        Assert.Equal(77_000, vehicle.Attributes.BatteryCapacityWh);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.ReadStateOfCharge | Capabilities.StartStop, vehicle.Capabilities);
        Assert.Equal(Interval, vehicle.State!.ReportInterval);

        await WaitForAnotherPollAsync();
        await Task.Delay(1500, Ct); // let the telemetry consumer drain

        var again = Assert.Single(await DevicesAsync());
        Assert.Equal(vehicle.Id, again.Id);
        Assert.Equal(1, await ReadingsAsync(vehicle.Id)); // the simulator's clock stood still: the same state twice is one reading
        var account = (await AccountAsync()).ToJson();
        Assert.DoesNotContain(SimulatorHost.SeedA, account, StringComparison.Ordinal);
        await WorkerHost.StopAsync(worker);
    }

    [Fact]
    public async Task State_of_charge_rises_after_a_plug_in_and_a_start()
    {
        var worker = await StartWorkerAsync();
        var vehicle = await VehicleAsync();
        await _simulator!.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/plug-in", null);
        var account = new ProviderAccount(CarCloudProvider.Id, Owner, "acct_a");
        var command = new DeviceCommand(CommandId.Parse("cmd_start"), vehicle.Id, CommandType.StartCharging, "test:start:veh_a");

        var sent = await worker.Services.GetRequiredService<CarCloudProvider>().SendAsync(command, new DeviceAddress("veh_a", account), Ct);
        _simulatorClock.Advance(TimeSpan.FromMinutes(30));

        Assert.IsType<SendResult.Accepted>(sent);
        var charged = await VehicleAsync(d => d.State?.LastReading?.SocPercent > 40m);
        Assert.Equal(DeviceStatus.Charging, charged.Status);
        Assert.Equal(47.14m, charged.State!.LastReading!.SocPercent);
        Assert.Equal(5_500, charged.State.LastReading.EnergyWh);
        await WorkerHost.EventuallyAsync(() => ReadingsAsync(vehicle.Id), n => n >= 2, TimeSpan.FromSeconds(10));
        await WorkerHost.StopAsync(worker);
    }

    [Fact]
    public async Task A_rotated_token_is_kept_across_a_worker_restart()
    {
        var first = await StartWorkerAsync();
        var vehicle = await VehicleAsync();
        await WorkerHost.StopAsync(first);

        // The simulator's access tokens expire: the restarted worker must refresh with the rotated token, not the used seed.
        _simulatorClock.Advance(TimeSpan.FromMinutes(16));
        var restarted = await StartWorkerAsync();

        var seenAgain = await VehicleAsync(d => d.State?.LastSeenAt > vehicle.State!.LastSeenAt);
        Assert.Equal(vehicle.Id, seenAgain.Id);
        Assert.False((await AccountAsync())["reconnectNeeded"].AsBoolean);
        Assert.False(_logs.Contains("needs to be linked again"), string.Join('\n', _logs.Lines));
        await WorkerHost.StopAsync(restarted);
    }

    [Fact]
    public async Task A_vehicle_whose_vendor_stops_answering_goes_offline_after_three_intervals()
    {
        var worker = await StartWorkerAsync();
        var vehicle = await VehicleAsync();
        var lastSeen = vehicle.State!.LastSeenAt!.Value;

        await _simulator!.DisposeAsync();
        _simulator = null;
        await WaitForAnotherPollAsync();

        var silent = Assert.Single(await DevicesAsync());
        Assert.Equal(lastSeen, silent.State!.LastSeenAt);
        Assert.NotEqual(DeviceStatus.Offline, silent.StatusAsOf(lastSeen + Interval));
        Assert.Equal(DeviceStatus.Offline, silent.StatusAsOf(lastSeen + (3 * Interval) + TimeSpan.FromSeconds(1)));
        Assert.True(_logs.Contains("carcloud"));
        await WorkerHost.StopAsync(worker);
    }
}
