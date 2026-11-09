using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Api;
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

    /// <summary>CP001's owner.</summary>
    public static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    private ChargerWorld(GatewayFactory gateway, IHost worker, OcppTestClient charger, DeviceId chargerId, IMongoDatabase database, ApiFactory? api, CapturingLoggerProvider workerLogs)
    {
        Gateway = gateway;
        Worker = worker;
        Charger = charger;
        ChargerId = chargerId;
        Database = database;
        Api = api;
        WorkerLogs = workerLogs;
    }

    public GatewayFactory Gateway { get; }

    /// <summary>Gets the API host on the same database, when the world was started with one.</summary>
    public ApiFactory? Api { get; }

    public IHost Worker { get; }

    /// <summary>Gets every log entry the worker wrote.</summary>
    public CapturingLoggerProvider WorkerLogs { get; }

    public OcppTestClient Charger { get; private set; }

    public DeviceId ChargerId { get; }

    public IMongoDatabase Database { get; }

    public CommandStore Commands => new(Database);

    /// <summary>Starts the gateway, connects and boots CP001, then starts the worker and, if asked, the API.</summary>
    /// <param name="mongo">The MongoDB fixture.</param>
    /// <param name="rabbit">The broker fixture.</param>
    /// <param name="settings">Worker settings.</param>
    /// <param name="hostSettings">Settings for every host (the API, the gateway and the worker).</param>
    /// <param name="withApi">Whether to start the API too; its execute key belongs to CP001's owner.</param>
    public static async Task<ChargerWorld> StartAsync(
        MongoFixture mongo,
        RabbitMqFixture rabbit,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyDictionary<string, string>? hostSettings = null,
        bool withApi = false)
    {
        var shared = hostSettings ?? new Dictionary<string, string>();
        var gateway = new GatewayFactory(mongo.ConnectionString, overrides: shared.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
        await gateway.StartAsync();
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);
        await charger.CallAsync(Boot);
        var device = await gateway.Devices.FindAsync("ocpp", "CP001", TestContext.Current.CancellationToken);

        var workerSettings = new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = $"worker-{Guid.NewGuid():N}",
            ["Commands:MonitorInterval"] = "00:00:00.200",
        };
        foreach (var (key, value) in shared.Concat(settings ?? new Dictionary<string, string>()))
        {
            workerSettings[key] = value;
        }

        var workerLogs = new CapturingLoggerProvider();
        var worker = await WorkerHost.StartAsync(mongo, rabbit, gateway.DatabaseName, gateway.QueuePrefix, workerSettings, workerLogs);

        ApiFactory? api = null;
        if (withApi)
        {
            var apiSettings = shared.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            apiSettings["ApiKeys:Keys:1:Owner"] = Owner.Value;
            api = new ApiFactory(mongo.ConnectionString, overrides: apiSettings) { DatabaseName = gateway.DatabaseName };
            await api.StartAsync();
        }

        return new ChargerWorld(gateway, worker, charger, device!.Id, mongo.Client.GetDatabase(gateway.DatabaseName), api, workerLogs);
    }

    /// <summary>Posts a command for CP001 through the API with its execute key; returns the accepted command's id.</summary>
    public async Task<CommandId> PostCommandAsync(CommandType type)
    {
        using var client = Api!.CreateClient(ApiFactory.ExecuteKeyA);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var body = new StringContent($$"""{"type":"{{type}}"}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"/v1/devices/{ChargerId}/commands", UriKind.Relative), body, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        return CommandId.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }

    /// <summary>Stores a direct command for the charger, due now, with its outbox entry; the relay takes it from there.</summary>
    public async Task<StoredCommand> CommandAsync(CommandType type, int? maxPowerW = null, string? traceparent = null)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new StoredCommand(
            Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), ChargerId, type, $"key_{Guid.NewGuid():N}", maxPowerW), now),
            Owner,
            traceparent);
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
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        await WorkerHost.StopAsync(Worker);
        Charger.Dispose();
        await Gateway.DisposeAsync();
    }
}
