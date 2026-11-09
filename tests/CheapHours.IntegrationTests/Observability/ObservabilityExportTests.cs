using System.Net;
using System.Net.Sockets;
using CheapHours.Core.Commands;
using CheapHours.IntegrationTests.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// OTLP export is switched by <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> alone, and a collector that is not there changes
/// nothing (observability-export spec).
/// </summary>
public sealed class ObservabilityExportTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static readonly string[] SignalPaths = ["/v1/traces", "/v1/metrics", "/v1/logs"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // observability-export: "Endpoint configured" and "Each role names itself".
    [Fact]
    public async Task Worker_exports_traces_metrics_and_logs_over_http_protobuf_named_cheaphours_worker()
    {
        using var receiver = OtlpReceiver.Start();
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(ObservabilityExportTests), Export(receiver.Url));

        var command = await worker.PendingAsync();
        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));

        await WorkerHost.EventuallyAsync(
            () => Task.FromResult(SignalPaths.Where(path => !receiver.Received(path, "cheaphours-worker")).ToArray()),
            missing => missing.Length == 0,
            TimeSpan.FromSeconds(30));
    }

    // observability-export: "Endpoint not configured". Signal-specific endpoints alone switch nothing on.
    [Fact]
    public async Task Without_the_endpoint_nothing_is_exported()
    {
        using var receiver = OtlpReceiver.Start();
        var settings = Export(receiver.Url);
        settings.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");
        foreach (var path in SignalPaths)
        {
            settings[$"OTEL_EXPORTER_OTLP_{path[4..].ToUpperInvariant()}_ENDPOINT"] = receiver.Url + path;
        }

        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(ObservabilityExportTests), settings);
        var command = await worker.PendingAsync();
        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Empty(receiver.Paths);
    }

    // observability-export: "Collector down". All three roles export to a port where nothing listens.
    [Fact]
    public async Task With_the_collector_down_a_posted_command_is_confirmed_and_the_roles_stay_ready()
    {
        var closed = ClosedPort();
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, hostSettings: Export($"http://127.0.0.1:{closed}", "grpc"), withApi: true);

        var id = await world.PostCommandAsync(CommandType.StartCharging);
        await world.AnswerNextCallAsync();
        await world.ReportAsync("Charging");

        await world.EventuallyAsync(id, c => c.Command.State == CommandState.Confirmed);
        using var gateway = world.Gateway.CreateClient();
        using var api = world.Api!.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await gateway.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode);
    }

    // Short export intervals, so a test sees each signal within seconds.
    private static Dictionary<string, string> Export(string endpoint, string protocol = "http/protobuf") => new()
    {
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol,
        ["OTEL_BSP_SCHEDULE_DELAY"] = "200",
        ["OTEL_BLRP_SCHEDULE_DELAY"] = "200",
        ["OTEL_METRIC_EXPORT_INTERVAL"] = "500",
    };

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
