using System.Diagnostics;
using CheapHours.Core.Ids;
using CheapHours.Host.Goals;
using CheapHours.Host.Prices;
using CheapHours.IntegrationTests.Goals;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Prices;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Locks;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static CheapHours.IntegrationTests.Prices.EnergyChartsFake;

namespace CheapHours.IntegrationTests.Observability;

/// <summary>
/// A poll and a price-import run each start a trace, and what they publish carries it (distributed-tracing:
/// "Scheduled work starts a trace").
/// </summary>
public sealed class ScheduledTracingTests(MongoFixture mongo, RabbitMqFixture rabbit)
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly DateOnly Recorded = new(2026, 11, 2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // distributed-tracing: "Poll traced".
    [Fact]
    public async Task A_poll_is_a_root_span_whose_vendor_calls_and_published_readings_belong_to_it()
    {
        using var spans = new SpanRecorder();
        await using var simulator = await SimulatorHost.StartAsync(new FakeTimeProvider(TimeProvider.System.GetUtcNow()), SimulatorHost.DefaultSettings(longTokens: false));
        var keys = Directory.CreateTempSubdirectory("cheaphours-keys-").FullName;
        var settings = WorkerHost.FastPolling($"worker-{Guid.NewGuid():N}");
        settings["Providers:CarCloud:BaseAddress"] = simulator.BaseAddress.ToString();
        settings["Providers:CarCloud:TokenEndpoint"] = new Uri(simulator.BaseAddress, "oauth/token").ToString();
        settings["Providers:CarCloud:ClientId"] = SimulatorHost.ClientId;
        settings["Providers:CarCloud:ClientSecret"] = SimulatorHost.ClientSecret;
        settings["Providers:CarCloud:PollInterval"] = "00:00:05";
        settings["Providers:CarCloud:Accounts:0:Owner"] = Owner.Value;
        settings["Providers:CarCloud:Accounts:0:ExternalAccountId"] = "acct_a";
        settings["Providers:CarCloud:Accounts:0:RefreshToken"] = SimulatorHost.SeedA;
        settings["DataProtection:KeysPath"] = keys;
        var worker = await WorkerHost.StartAsync(mongo, rabbit, MongoFixture.NewDatabaseName(nameof(ScheduledTracingTests)), RabbitMqFixture.NewPrefix(nameof(ScheduledTracingTests)), settings);
        try
        {
            var vendor = simulator.BaseAddress.Authority;
            var poll = await spans.WaitForAsync(
                a => a.DisplayName == "poll carcloud"
                    && spans.InTrace(a.TraceId) is var trace
                    && trace.Any(s => Url(s).Contains(vendor, StringComparison.Ordinal))
                    && trace.Any(s => s.Kind == ActivityKind.Producer && Equals(s.GetTagItem("cheaphours.message.type"), TelemetryReceived.TypeName)),
                TimeSpan.FromSeconds(60));

            var spansOfPoll = spans.InTrace(poll.TraceId);
            Assert.Equal(default, poll.ParentSpanId);
            Assert.Equal("carcloud", poll.GetTagItem("cheaphours.provider.id"));
            var calls = spansOfPoll.Where(a => a.Source.Name == "System.Net.Http").ToArray();
            Assert.NotEmpty(calls);
            Assert.All(calls, call => Assert.Equal(poll.SpanId, call.ParentSpanId));
            var publishes = spansOfPoll.Where(a => a.Kind == ActivityKind.Producer && Equals(a.GetTagItem("cheaphours.message.type"), TelemetryReceived.TypeName)).ToArray();
            Assert.All(publishes, publish => Assert.Equal(poll.SpanId, publish.ParentSpanId));
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
            Directory.Delete(keys, recursive: true);
        }
    }

    // distributed-tracing: "Prices re-plan in the import's trace".
    [Fact]
    public async Task Re_planning_on_published_prices_continues_the_trace_of_the_import_run()
    {
        using var spans = new SpanRecorder();
        using var source = new EnergyChartsFake();
        source.Script(Recorded, Answer.Ok(Fixture(Recorded)));
        var databaseName = MongoFixture.NewDatabaseName(nameof(ScheduledTracingTests));
        var worker = await WorkerHost.StartAsync(mongo, rabbit, databaseName, RabbitMqFixture.NewPrefix(nameof(ScheduledTracingTests)), new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = $"worker-{Guid.NewGuid():N}",
            ["Prices:BaseUrl"] = source.BaseUrl.ToString(),
            ["Prices:PauseBetweenRequests"] = "00:00:00",
            ["Prices:RetryBaseDelay"] = "00:00:00.010",
        });
        try
        {
            var database = mongo.Client.GetDatabase(databaseName);
            var now = DateTimeOffset.UtcNow;
            var (vehicle, charger) = await GoalSeeds.CarAndWallboxAsync(database, Owner, "traced");
            await GoalSeeds.PricesAsync(database, now, now.AddHours(13), 100m);
            var planned = Assert.IsType<GoalOutcome.Planned>(await worker.Services.GetRequiredService<GoalPlanningService>().CreateAsync(Owner, new GoalRequest(vehicle, charger, 80, now.AddHours(13)), Ct));

            // 13:00 Berlin on 1 November: the run imports and announces the recorded 2 November.
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
            var job = ActivatorUtilities.CreateInstance<PriceImportJob>(worker.Services, worker.Services.GetRequiredKeyedService<ILease>(LeaseNames.PriceImport), (TimeProvider)clock);
            Assert.True(await job.RunOnceAsync(Ct));

            var replan = await spans.WaitForAsync(a => a.DisplayName == "goal replan" && Equals(a.GetTagItem("cheaphours.goal.id"), planned.Goal.Goal.Id.Value));
            var trace = spans.InTrace(replan.TraceId);
            var import = Assert.Single(trace, a => a.DisplayName == "price-import");
            var publish = Assert.Single(trace, a => a.Kind == ActivityKind.Producer && Equals(a.GetTagItem("cheaphours.message.type"), PricesPublished.TypeName));
            var process = Assert.Single(trace, a => a.Kind == ActivityKind.Consumer);
            Assert.Equal(default, import.ParentSpanId);
            Assert.Equal(import.SpanId, publish.ParentSpanId);
            Assert.Equal(import.SpanId, process.ParentSpanId);
            Assert.Equal(process.SpanId, replan.ParentSpanId);
            Assert.Equal($"{worker.Services.GetRequiredService<MessagingTopology>().Planner.Main} process", process.DisplayName);
        }
        finally
        {
            await WorkerHost.StopAsync(worker);
        }
    }

    private static string Url(Activity span) => span.GetTagItem("url.full")?.ToString() ?? string.Empty;
}
