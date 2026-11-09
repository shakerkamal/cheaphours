using System.Diagnostics;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.IntegrationTests.Commands;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Tests.Observability;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// A vehicle command's spans on a real worker against the car-cloud vendor fake (distributed-tracing: "Vehicle
/// command end to end", "Spans never carry secrets"; structured-logging: "Logs written inside a span carry its
/// trace", "Command logs name the command, device and provider").
/// </summary>
public sealed class CommandDispatchTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vehicle_command_is_published_dispatched_sent_and_confirmed_in_the_trace_stored_on_it()
    {
        using var spans = new SpanRecorder();
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandDispatchTracingTests));
        var (traceparent, stored) = NewContext();

        var command = await worker.PendingAsync(traceparent: traceparent);
        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        await worker.ReportStatusAsync(DeviceStatus.Charging);
        await worker.Monitor.RunOnceAsync(Ct);
        Assert.Equal(CommandState.Confirmed, (await worker.ReadAsync(command.Id)).Command.State);

        var trace = await spans.WaitForTraceAsync(stored.TraceId, ["dispatch StartCharging", "command confirm"]);
        var publish = Assert.Single(trace, a => a.Kind == ActivityKind.Producer);
        var dispatch = Assert.Single(trace, a => a.DisplayName == "dispatch StartCharging");
        var process = Assert.Single(trace, a => a.SpanId == dispatch.ParentSpanId);
        var send = Assert.Single(trace, a => a.Source.Name == "System.Net.Http" && Url(a).EndsWith("/charging/start", StringComparison.Ordinal));
        var confirm = Assert.Single(trace, a => a.DisplayName == "command confirm");

        Assert.Equal(stored.SpanId, publish.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, process.Kind);
        Assert.Equal(stored.SpanId, process.ParentSpanId);
        Assert.Equal(dispatch.SpanId, send.ParentSpanId);
        Assert.Equal(stored.SpanId, confirm.ParentSpanId);
        Assert.Equal(command.Id.Value, dispatch.GetTagItem("cheaphours.command.id"));
        Assert.Equal(worker.Vehicle.Value, dispatch.GetTagItem("cheaphours.device.id"));
        Assert.Equal("carcloud", dispatch.GetTagItem("cheaphours.provider.id"));
        Assert.Equal("accepted", dispatch.GetTagItem("cheaphours.outcome"));

        // The dispatcher's log entry about this send names the command, device and provider, and the dispatch span.
        var applied = Assert.Single(worker.Logs.Entries, e => e.Category.EndsWith(".CommandDispatcher", StringComparison.Ordinal) && e.Values.GetValueOrDefault("CommandId") == command.Id.Value);
        Assert.Equal(command.Id.Value, applied.Scopes["commandId"]);
        Assert.Equal(worker.Vehicle.Value, applied.Scopes["deviceId"]);
        Assert.Equal("carcloud", applied.Scopes["providerId"]);
        Assert.Equal(dispatch.TraceId.ToString(), applied.Scopes["TraceId"]);
        Assert.Equal(dispatch.SpanId.ToString(), applied.Scopes["SpanId"]);
    }

    [Fact]
    public async Task A_dispatch_refreshed_after_a_401_leaves_no_token_or_authorization_header_in_any_span()
    {
        using var spans = new SpanRecorder();
        await using var worker = await VendorCloudWorker.StartAsync(mongo, rabbit, nameof(CommandDispatchTracingTests));
        var first = await worker.PendingAsync();
        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(first.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));
        var sent = await worker.ReadAsync(first.Id);
        sent.Command.Confirm(DateTimeOffset.UtcNow);
        await worker.Commands.SaveAsync(sent, Ct);
        worker.Vendor.RejectCurrentAccessTokens();
        var (traceparent, stored) = NewContext();

        var command = await worker.PendingAsync(traceparent: traceparent);
        await WorkerHost.EventuallyAsync(() => worker.ReadAsync(command.Id), c => c.Command.State == CommandState.Sent, TimeSpan.FromSeconds(30));

        var trace = await spans.WaitForTraceAsync(stored.TraceId, ["dispatch StartCharging"]);
        Assert.Contains(trace, a => a.Source.Name == "System.Net.Http" && Url(a).EndsWith("/oauth/token", StringComparison.Ordinal));
        Assert.Contains(trace, a => a.Source.Name == "System.Net.Http" && Equals(a.GetTagItem("http.response.status_code"), 401));
        var secrets = worker.Vendor.AllSecrets();
        Assert.NotEmpty(secrets);
        foreach (var span in spans.All)
        {
            var texts = Texts(span).ToArray();
            Assert.DoesNotContain(texts, text => secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)));
        }

        Assert.DoesNotContain(trace.SelectMany(a => a.TagObjects), t => t.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    private static (string Traceparent, ActivityContext Context) NewContext()
    {
        var traceparent = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";
        return (traceparent, ActivityContext.Parse(traceparent, null));
    }

    private static string Url(Activity span) => span.GetTagItem("url.full")?.ToString() ?? string.Empty;

    // Everything a span would export as text: its name, status, tags, and its events' names and tags.
    private static IEnumerable<string> Texts(Activity span) =>
        new[] { span.DisplayName, span.StatusDescription ?? string.Empty }
            .Concat(span.TagObjects.Select(t => $"{t.Key}={t.Value}"))
            .Concat(span.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}").Append(e.Name)));
}
