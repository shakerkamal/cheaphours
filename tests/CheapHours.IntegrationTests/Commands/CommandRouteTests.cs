using System.Text;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.Ocpp;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>The two routes (command-dispatch spec: a command takes the route that reaches its device).</summary>
public sealed class CommandRouteTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private VendorCloudWorker _worker = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandRouteTests));

    public async ValueTask DisposeAsync() => await _worker.DisposeAsync();

    // command-dispatch: "Car-cloud vehicle".
    [Fact]
    public async Task Vehicle_command_reaches_the_car_cloud_once_with_its_idempotency_key()
    {
        var command = await _worker.PublishedAsync();
        var device = (await new DeviceStateStore(_worker.Database).GetAsync(_worker.Vehicle, Ct))!;
        var route = Route<ProviderCommandRoute>();

        Assert.True(route.Reaches(device));
        var outcome = await route.SendAsync(command, device, Ct);

        var completed = Assert.IsType<RouteOutcome.Completed>(outcome);
        Assert.IsType<SendResult.Accepted>(completed.Result);
        var request = Assert.Single(_worker.CommandRequests);
        Assert.Equal("/api/v1/vehicles/veh_fake/charging/start", request.Path);
        Assert.Equal(command.Command.IdempotencyKey, request.Header("Idempotency-Key"));
    }

    // command-dispatch: "Charger held by a replica".
    [Fact]
    public async Task Charger_command_is_published_to_the_queue_of_the_replica_holding_it()
    {
        var topology = _worker.Host.Services.GetRequiredService<MessagingTopology>();
        var queue = topology.GatewayCommandQueue("gw-0");
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbit.ConnectionString) }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
        await new ConnectionRegistry(_worker.Database).RecordAsync(new ConnectionEntry("CP-ROUTE", "gw-0", Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);
        var command = Command(Charger("CP-ROUTE").Device.Id);

        var outcome = await Route<GatewayCommandRoute>().SendAsync(command, Charger("CP-ROUTE"), Ct);

        Assert.IsType<RouteOutcome.Forwarded>(outcome);
        var got = await channel.BasicGetAsync(queue, autoAck: true, Ct);
        Assert.NotNull(got);
        Assert.Equal("GatewayCommand", got.BasicProperties.Type);
        var body = Encoding.UTF8.GetString(got.Body.Span);
        Assert.Contains("\"chargePointId\":\"CP-ROUTE\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"idempotencyKey\":\"{command.Command.IdempotencyKey}\"", body, StringComparison.Ordinal);
    }

    // command-dispatch: "Charger not connected".
    [Fact]
    public async Task Charger_with_no_connection_is_transient()
    {
        var outcome = await Route<GatewayCommandRoute>().SendAsync(Command(Charger("CP-NONE").Device.Id), Charger("CP-NONE"), Ct);

        var transient = Assert.IsType<SendResult.Transient>(Assert.IsType<RouteOutcome.Completed>(outcome).Result);
        Assert.Equal(GatewayCommandRoute.NotConnected, transient.Reason);
    }

    [Fact]
    public async Task Charger_held_by_a_replica_whose_queue_is_gone_is_transient()
    {
        await new ConnectionRegistry(_worker.Database).RecordAsync(new ConnectionEntry("CP-GONE", "gw-9", Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);

        var outcome = await Route<GatewayCommandRoute>().SendAsync(Command(Charger("CP-GONE").Device.Id), Charger("CP-GONE"), Ct);

        Assert.IsType<SendResult.Transient>(Assert.IsType<RouteOutcome.Completed>(outcome).Result);
    }

    [Fact]
    public void Each_device_kind_has_one_route()
    {
        var vehicle = new StoredDevice(new Device(DeviceId.Parse("dev_v"), VendorCloudWorker.Owner, "carcloud", "veh", DeviceType.Vehicle, Capabilities.StartStop, new DeviceAttributes(77_000, null)), null, null, null);
        var heatPump = new StoredDevice(new Device(DeviceId.Parse("dev_h"), VendorCloudWorker.Owner, "heatpumpcloud", "hp", DeviceType.HeatPump, Capabilities.None, new DeviceAttributes(null, null)), null, null, null);

        Assert.Equal((true, false), (Route<ProviderCommandRoute>().Reaches(vehicle), Route<GatewayCommandRoute>().Reaches(vehicle)));
        Assert.Equal((false, true), (Route<ProviderCommandRoute>().Reaches(Charger("CP")), Route<GatewayCommandRoute>().Reaches(Charger("CP"))));
        Assert.Equal((false, false), (Route<ProviderCommandRoute>().Reaches(heatPump), Route<GatewayCommandRoute>().Reaches(heatPump)));
    }

    private static StoredDevice Charger(string chargePointId) => new(
        new Device(DeviceId.Parse($"dev_{chargePointId.ToLowerInvariant()}"), VendorCloudWorker.Owner, OcppProvider.Id, chargePointId, DeviceType.Charger, Capabilities.StartStop | Capabilities.SetPowerLimit, new DeviceAttributes(null, 11_000)),
        null,
        null,
        null);

    private static StoredCommand Command(DeviceId device) => new(
        Core.Commands.Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), device, CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), DateTimeOffset.UtcNow),
        VendorCloudWorker.Owner,
        null);

    private T Route<T>()
        where T : ICommandRoute => _worker.Host.Services.GetRequiredService<T>();
}
