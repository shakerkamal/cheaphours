using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Persistence.Commands;
using Microsoft.Extensions.Hosting;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// The whole command path for an OCPP charger: a real gateway on Kestrel and a real worker on the same database and
/// broker prefix, and test charger <c>CP001</c> connected and booted. The test plays the charger's part.
/// </summary>
internal sealed class ChargerWorld : IAsyncDisposable
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";

    private ChargerWorld(GatewayFactory gateway, IHost worker, OcppTestClient charger, DeviceId chargerId, IMongoDatabase database)
    {
        Gateway = gateway;
        Worker = worker;
        Charger = charger;
        ChargerId = chargerId;
        Database = database;
    }

    public GatewayFactory Gateway { get; }

    public IHost Worker { get; }

    public OcppTestClient Charger { get; private set; }

    public DeviceId ChargerId { get; }

    public IMongoDatabase Database { get; }

    public CommandStore Commands => new(Database);

    public static async Task<ChargerWorld> StartAsync(MongoFixture mongo, RabbitMqFixture rabbit, IReadOnlyDictionary<string, string>? settings = null)
    {
        var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        var device = await gateway.Devices.FindAsync("ocpp", "CP001", TestContext.Current.CancellationToken);

        var workerSettings = new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = $"worker-{Guid.NewGuid():N}",
            ["Commands:MonitorInterval"] = "00:00:00.200",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            workerSettings[key] = value;
        }

        var worker = await WorkerHost.StartAsync(mongo, rabbit, gateway.DatabaseName, gateway.QueuePrefix, workerSettings);
        return new ChargerWorld(gateway, worker, charger, device!.Id, mongo.Client.GetDatabase(gateway.DatabaseName));
    }

    /// <summary>Stores a direct command for the charger, due now, with its outbox entry; the relay takes it from there.</summary>
    public async Task<StoredCommand> CommandAsync(CommandType type, int? maxPowerW = null)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), ChargerId, type, $"key_{Guid.NewGuid():N}", maxPowerW), now),
            OwnerId.Parse("own_demo"),
            null);
        Assert.True(await Commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), TestContext.Current.CancellationToken));
        return command;
    }

    public async Task<StoredCommand> ReadAsync(CommandId id) => (await Commands.FindAsync(id, TestContext.Current.CancellationToken))!;

    public Task<StoredCommand> EventuallyAsync(CommandId id, Func<StoredCommand, bool> condition, int seconds = 30) =>
        WorkerHost.EventuallyAsync(() => ReadAsync(id), condition, TimeSpan.FromSeconds(seconds));

    /// <summary>Waits for the gateway's next call to the charger and answers it with <paramref name="status"/>.</summary>
    /// <returns>The call's action and payload.</returns>
    public async Task<(string Action, string Payload)> AnswerNextCallAsync(string status = "Accepted")
    {
        var call = await Charger.ReceiveAsync() ?? throw new InvalidOperationException("The gateway closed the charger's connection.");
        await Charger.SendAsync($$"""[3,"{{call[1]!.GetValue<string>()}}",{"status":"{{status}}"}]""");
        return (call[2]!.GetValue<string>(), call[3]!.ToJsonString());
    }

    /// <summary>Reports a status as the charger, timestamped now, and waits for the gateway's answer.</summary>
    public Task ReportAsync(string status) =>
        Charger.CallAsync($$"""[2,"s-{{Guid.NewGuid():N}}","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"{{status}}","timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}}"}]""");

    /// <summary>Replaces the charger with a new connection (the old socket is dropped without a handshake).</summary>
    public async Task ReconnectAsync()
    {
        Charger.Socket.Abort();
        Charger.Dispose();
        Charger = await OcppTestClient.ConnectAsync(Gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await Charger.CallAsync("""[2,"h-reconnect","Heartbeat",{}]""");
    }

    public async ValueTask DisposeAsync()
    {
        await WorkerHost.StopAsync(Worker);
        Charger.Dispose();
        await Gateway.DisposeAsync();
    }
}
