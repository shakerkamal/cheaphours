using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using CheapHours.Core.Commands;
using CheapHours.Host.Gateway;
using CheapHours.IntegrationTests.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Providers.Kit;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// OCPP command calls are measured like vendor calls, and each replica reports the chargers it holds
/// (operational-metrics: "OCPP command calls are measured like vendor calls", "Connected chargers are reported per
/// replica").
/// </summary>
public sealed class GatewayMetricsTests(MongoFixture mongo)
{
    private const string Boot = """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""";
    private static readonly (string, string?) Ocpp = ("provider.id", "ocpp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // operational-metrics: "Charger accepts".
    [Fact]
    public async Task An_answered_call_is_counted_as_success_and_timed()
    {
        await using var gateway = await StartAsync();
        using var metrics = Record(gateway);
        await using var commands = await GatewayCommands.StartAsync(gateway);
        using var charger = await ConnectAsync(gateway, "CP001", GatewayFactory.Password);
        var command = GatewayCommands.Command(CommandType.StartCharging);

        await commands.SendAsync(command);
        var call = await charger.ReceiveAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");
        await commands.ResultsAsync(command.Id);

        Assert.Equal(1, metrics.Sum(ProviderMetrics.RequestsName, Ocpp, ("outcome", "success")));
        var duration = Assert.Single(metrics.Of(ProviderMetrics.DurationName, Ocpp, ("outcome", "success"))).Value;
        Assert.InRange(duration, 0.2, 5);
    }

    // operational-metrics: "Charger never answers".
    [Fact]
    public async Task A_call_without_an_answer_is_counted_as_transient()
    {
        await using var gateway = await StartAsync(new Dictionary<string, string?> { ["Gateway:Commands:CallTimeout"] = "00:00:01" });
        using var metrics = Record(gateway);
        await using var commands = await GatewayCommands.StartAsync(gateway);
        using var charger = await ConnectAsync(gateway, "CP001", GatewayFactory.Password);
        await charger.CallAsync($$"""[2,"st1","StartTransaction",{"connectorId":1,"idTag":"CHEAPHOURS","meterStart":0,"timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}"}]""");
        var command = GatewayCommands.Command(CommandType.StopCharging);

        await commands.SendAsync(command);
        await charger.ReceiveAsync();
        await commands.ResultsAsync(command.Id);

        Assert.Equal(1, metrics.Sum(ProviderMetrics.RequestsName, Ocpp, ("outcome", "transient")));
        Assert.Empty(metrics.Of(ProviderMetrics.RequestsName, Ocpp, ("outcome", "success")));
    }

    // operational-metrics: "Already in effect".
    [Fact]
    public async Task A_command_already_in_effect_makes_no_call_and_counts_none()
    {
        await using var gateway = await StartAsync();
        using var metrics = Record(gateway);
        await using var commands = await GatewayCommands.StartAsync(gateway);
        using var charger = await ConnectAsync(gateway, "CP001", GatewayFactory.Password);
        var command = GatewayCommands.Command(CommandType.StopCharging);

        await commands.SendAsync(command);

        Assert.IsType<SendResult.Accepted>(Assert.Single(await commands.ResultsAsync(command.Id)));
        Assert.Empty(metrics.Of(ProviderMetrics.RequestsName));
        Assert.Empty(metrics.Of(ProviderMetrics.DurationName));
    }

    // operational-metrics: "Two chargers, one leaves".
    [Fact]
    public async Task The_connections_gauge_follows_the_chargers_the_replica_holds()
    {
        await using var gateway = await StartAsync();
        using var metrics = Record(gateway);
        using var first = await ConnectAsync(gateway, "CP001", GatewayFactory.Password);
        using var second = await ConnectAsync(gateway, "CP002", GatewayFactory.OtherPassword);

        Assert.Equal(2, metrics.Latest(GatewayMetrics.ConnectionsName, Ocpp));

        await second.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        for (var i = 0; i < 50 && metrics.Latest(GatewayMetrics.ConnectionsName, Ocpp) != 1; i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Equal(1, metrics.Latest(GatewayMetrics.ConnectionsName, Ocpp));
    }

    private async Task<GatewayFactory> StartAsync(Dictionary<string, string?>? overrides = null)
    {
        var gateway = new GatewayFactory(mongo.ConnectionString, overrides: overrides);
        await gateway.StartAsync();
        return gateway;
    }

    private static MetricRecorder Record(GatewayFactory gateway) =>
        new(gateway.Services.GetRequiredService<IMeterFactory>(), ProviderMetrics.RequestsName, ProviderMetrics.DurationName, GatewayMetrics.ConnectionsName);

    private static async Task<OcppTestClient> ConnectAsync(GatewayFactory gateway, string chargePointId, string password)
    {
        var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri(chargePointId), chargePointId, password);
        await charger.CallAsync(Boot);
        return charger;
    }
}
