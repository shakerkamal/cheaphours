using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using CheapHours.Core;
using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Commands;
using CheapHours.Host.Gateway;
using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Api;
using CheapHours.IntegrationTests.Commands;
using CheapHours.IntegrationTests.Gateway;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Plans;
using CheapHours.Providers.Kit;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// One trace from the API call to the charger's confirmation, across the API, the worker and the gateway
/// (distributed-tracing: "One trace follows a command to its confirmation", "Spans never carry secrets";
/// structured-logging: "Logs never carry secrets or bodies").
/// </summary>
public sealed class CommandTraceTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private const string RequestBody = """{"type":"StartCharging"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // distributed-tracing: "Charger command end to end" and "Authenticated API request".
    [Fact]
    public async Task A_charger_command_posted_through_the_api_is_one_trace_from_the_request_to_its_confirmation()
    {
        using var spans = new SpanRecorder();
        await using var world = await ChargerWorld.StartAsync(mongo, rabbit, withApi: true);

        var id = await world.PostCommandAsync(CommandType.StartCharging);
        var call = await world.AnswerNextCallAsync();
        await world.ReportAsync("Charging");
        var confirmed = await world.EventuallyAsync(id, c => c.Command.State == CommandState.Confirmed);

        Assert.Equal("RemoteStartTransaction", call.Action);
        var stored = ActivityContext.Parse(confirmed.Traceparent!, null);
        var topology = world.Worker.Services.GetRequiredService<MessagingTopology>();
        var trace = await spans.WaitForTraceAsync(
            stored.TraceId,
            [
                "dispatch StartCharging",
                "ocpp RemoteStartTransaction",
                "command confirm",
                $"{topology.Dispatch.Main} process",
                $"{world.Gateway.Topology.GatewayCommandQueue(GatewayFactory.ReplicaId)} process",
                $"{topology.CommandResults.Main} process",
            ]);

        // The request span is the root, and it is the context stored on the command.
        var request = Assert.Single(trace, a => a.Source.Name == "Microsoft.AspNetCore");
        Assert.Equal(ActivityKind.Server, request.Kind);
        Assert.Equal(default, request.ParentSpanId);
        Assert.Equal(stored.SpanId, request.SpanId);
        Assert.Contains(trace, a => a.Kind == ActivityKind.Producer && Equals(a.GetTagItem("cheaphours.message.type"), CommandRequested.TypeName));
        Assert.Contains(trace, a => a.Kind == ActivityKind.Producer && Equals(a.GetTagItem("cheaphours.message.type"), GatewayCommand.TypeName));
        Assert.Contains(trace, a => a.Kind == ActivityKind.Producer && Equals(a.GetTagItem("cheaphours.message.type"), GatewayCommandResult.TypeName));

        // No span holds the API key, its hash or the request body.
        var secrets = new[] { ApiFactory.ExecuteKeyA, ApiFactory.Hash(ApiFactory.ExecuteKeyA), RequestBody, GatewayFactory.Password };
        Assert.DoesNotContain(spans.All.SelectMany(Texts), text => secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)));
        Assert.DoesNotContain(trace.SelectMany(a => a.TagObjects), t => t.Key.Contains("api-key", StringComparison.OrdinalIgnoreCase) || t.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase));

        // No log line of the three roles holds the API key, its hash or the charger's password.
        var lines = world.Api!.Logs.Lines.Concat(world.Gateway.Logs.Lines).Concat(world.WorkerLogs.Lines).ToArray();
        Assert.NotEmpty(lines);
        var logSecrets = new[] { ApiFactory.ExecuteKeyA, ApiFactory.Hash(ApiFactory.ExecuteKeyA), GatewayFactory.Password, GatewayFactory.Hash(GatewayFactory.Password) };
        Assert.DoesNotContain(lines, line => logSecrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
    }

    // distributed-tracing: "Plan confirmation".
    [Fact]
    public async Task Commands_made_by_a_plan_confirmation_store_the_trace_of_the_confirm_request()
    {
        using var spans = new SpanRecorder();
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();
        var database = mongo.Client.GetDatabase(api.DatabaseName);
        var departure = SlotGrid.Floor(DateTimeOffset.UtcNow).AddHours(13);
        var first = departure.AddHours(-6);
        var goal = await PlanSeeds.GoalAsync(database, OwnerId.Parse("own_a"), departure, 1m, 2m, slotStarts: [first, first.AddHours(2)]);
        var planId = (await new PlanStore(database).GetCurrentAsync(goal, Ct))!.Plan.Id;
        using var client = api.CreateClient(ApiFactory.ExecuteKeyA);
        using var confirm = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/plans/{planId}/confirm", UriKind.Relative));
        confirm.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(confirm, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var commands = await new CommandStore(database).ListForPlanAsync(planId, Ct);
        Assert.Equal(4, commands.Count);
        var contexts = commands.Select(c => ActivityContext.Parse(c.Traceparent!, null)).ToArray();
        var traceId = Assert.Single(contexts.Select(c => c.TraceId).Distinct());
        var request = await spans.WaitForAsync(a => a.TraceId == traceId && a.Source.Name == "Microsoft.AspNetCore");
        Assert.Equal(ActivityKind.Server, request.Kind);
        Assert.All(contexts, c => Assert.Equal(request.SpanId, c.SpanId));
    }

    // operational-metrics: "Metric tags stay bounded and safe" ("Many devices"). Fifty chargers each confirm a start.
    [Fact]
    public async Task Metrics_carry_only_bounded_tags_and_fifty_chargers_make_one_confirmation_series()
    {
        string[] allowed = ["provider.id", "outcome", "command.type", "reason"];
        string[] instruments =
        [
            CommandMetrics.ConfirmationDurationName, CommandMetrics.DeadLetteredName, CommandMetrics.OutboxLagName,
            TelemetryMetrics.ReadingsName, GatewayMetrics.ConnectionsName, ProviderMetrics.RequestsName,
            ProviderMetrics.DurationName, ProviderMetrics.CircuitStateName,
        ];
        var ids = Enumerable.Range(1, 50).Select(i => $"CP{i:000}").ToArray();
        var overrides = new Dictionary<string, string?>();
        for (var i = 0; i < ids.Length; i++)
        {
            overrides[$"Providers:Ocpp:ChargePoints:{i}:Id"] = ids[i];
            overrides[$"Providers:Ocpp:ChargePoints:{i}:OwnerId"] = ChargerWorld.Owner.Value;
            overrides[$"Providers:Ocpp:ChargePoints:{i}:PasswordHash"] = GatewayFactory.Hash(GatewayFactory.Password);
            overrides[$"Providers:Ocpp:ChargePoints:{i}:MaxPowerW"] = "11000";
        }

        using var everything = new MetricRecorder(null, instruments);
        await using var gateway = new GatewayFactory(mongo.ConnectionString, overrides: overrides);
        await gateway.StartAsync();
        var worker = await WorkerHost.StartAsync(mongo, rabbit, gateway.DatabaseName, gateway.QueuePrefix, new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = $"worker-{Guid.NewGuid():N}",
            ["Commands:MonitorInterval"] = "00:00:00.200",
        });
        using var confirmations = new MetricRecorder(worker.Services.GetRequiredService<IMeterFactory>(), CommandMetrics.ConfirmationDurationName);
        var chargers = new List<OcppTestClient>();
        try
        {
            chargers.AddRange(await Task.WhenAll(ids.Select(async id =>
            {
                var charger = await OcppTestClient.ConnectAsync(gateway.OcppUri(id), id, GatewayFactory.Password);
                await charger.CallAsync("""[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP"}]""");
                return charger;
            })));
            var store = new CommandStore(mongo.Client.GetDatabase(gateway.DatabaseName));
            var commands = new List<CommandId>();
            foreach (var id in ids)
            {
                var device = await gateway.Devices.FindAsync("ocpp", id, Ct);
                var now = DateTimeOffset.UtcNow;
                var command = new StoredCommand(Command.Create(new DeviceCommand(CommandId.New(TimeProvider.System), device!.Id, CommandType.StartCharging, $"key_{Guid.NewGuid():N}"), now), ChargerWorld.Owner, null);
                Assert.True(await store.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), Ct));
                commands.Add(command.Id);
            }

            await Task.WhenAll(chargers.Select(async charger =>
            {
                // Fifty dispatches can take a while on a loaded machine running the whole suite.
                var call = await charger.ReceiveAsync(TimeSpan.FromMinutes(2));
                await charger.SendAsync($$"""[3,"{{call![1]!.GetValue<string>()}}",{"status":"Accepted"}]""");
                var status = await charger.CallAsync($$"""[2,"s-{{Guid.NewGuid():N}}","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Charging","timestamp":"{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}}"}]""");
                Assert.True(status[0]!.GetValue<int>() == 3, $"The gateway did not take the StatusNotification: {status.ToJsonString()}");
            }));
            foreach (var id in commands)
            {
                await ConfirmedAsync(store, gateway.DatabaseName, id);
            }

            var series = (await WaitForCountAsync(confirmations, CommandMetrics.ConfirmationDurationName, 50))
                .Select(m => string.Join(",", m.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}")))
                .Distinct(StringComparer.Ordinal);
            Assert.Equal(["command.type=StartCharging,provider.id=ocpp"], series);
            everything.Collect();
            Assert.NotEmpty(everything.All);
            Assert.All(everything.All, m => Assert.Empty(m.Tags.Keys.Except(allowed, StringComparer.Ordinal)));
        }
        finally
        {
            chargers.ForEach(c => c.Dispose());
            await WorkerHost.StopAsync(worker);
        }
    }

    // Waits up to a minute for the command to be confirmed; if it is not, fails with its history and its device as
    // stored, which is what the confirmation compares (the report's receipt time against the send).
    private async Task ConfirmedAsync(CommandStore store, string database, CommandId id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var stored = (await store.FindAsync(id, Ct))!;
        while (stored.Command.State != CommandState.Confirmed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, Ct);
            stored = (await store.FindAsync(id, Ct))!;
        }

        if (stored.Command.State == CommandState.Confirmed)
        {
            return;
        }

        var device = await mongo.Client.GetDatabase(database).GetCollection<BsonDocument>(MongoIndexes.Devices)
            .Find(new BsonDocument("_id", stored.Command.Request.DeviceId.Value)).FirstOrDefaultAsync(Ct);
        var history = string.Join(", ", stored.Command.History.Select(h => $"{h.State}@{h.At:O}"));
        Assert.Fail($"Command {id} is {stored.Command.State} after 60 s. History: {history}. Device: {device?.ToJson()}");
    }

    private static async Task<IReadOnlyList<Measurement>> WaitForCountAsync(MetricRecorder recorder, string instrument, int count)
    {
        for (var i = 0; i < 100 && recorder.Of(instrument).Count < count; i++)
        {
            await Task.Delay(100, Ct);
        }

        return recorder.Of(instrument);
    }

    // Everything a span would export as text: its name, status, tags, and its events' names and tags.
    private static IEnumerable<string> Texts(Activity span) =>
        new[] { span.DisplayName, span.StatusDescription ?? string.Empty }
            .Concat(span.TagObjects.Select(t => $"{t.Key}={t.Value}"))
            .Concat(span.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}").Append(e.Name)));
}
