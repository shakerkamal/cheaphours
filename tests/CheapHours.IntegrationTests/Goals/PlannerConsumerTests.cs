using System.Net;
using System.Net.Sockets;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.Host.Goals;
using CheapHours.Host.Prices;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Messaging;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using CheapHours.Persistence.Prices;
using CheapHours.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MongoDb;

namespace CheapHours.IntegrationTests.Goals;

/// <summary>Re-planning on <c>PricesPublished</c> through the worker's planner consumer (design §6).</summary>
public sealed class PlannerConsumerTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");

    // One database for the class (each test uses its own goals and devices); a broker prefix per test.
    private static readonly string DatabaseName = MongoFixture.NewDatabaseName(nameof(PlannerConsumerTests));
    private readonly CapturingLoggerProvider _logs = new();
    private IHost _worker = null!;
    private IMongoDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GoalPlanningService Planning => _worker.Services.GetRequiredService<GoalPlanningService>();

    private PlanStore Plans => new(_database);

    public async ValueTask InitializeAsync()
    {
        _worker = await WorkerHost.StartAsync(mongo, rabbit, DatabaseName, RabbitMqFixture.NewPrefix(nameof(PlannerConsumerTests)), new Dictionary<string, string>(), _logs);
        _database = mongo.Client.GetDatabase(DatabaseName);
    }

    public async ValueTask DisposeAsync()
    {
        await WorkerHost.StopAsync(_worker);
        _logs.Dispose();
    }

    [Fact]
    public async Task Published_prices_re_plan_a_live_draft_and_a_confirmed_plan_but_not_an_expired_draft()
    {
        var now = DateTimeOffset.UtcNow;
        await GoalSeeds.PricesAsync(_database, now, now.AddHours(4), 300m);
        var draft = await CreateGoalAsync("a", now.AddHours(13));
        var confirmed = await CreateGoalAsync("b", now.AddHours(13));
        await GoalSeeds.ConfirmAsync(_database, confirmed.Plan.VersionId, now);
        var expired = await CreateGoalAsync("c", now.AddHours(13));
        await GoalSeeds.ExpireAsync(_database, expired.Plan.VersionId);

        // The rest of the window gets cheaper prices, then the day is announced.
        await GoalSeeds.PricesAsync(_database, now.AddHours(4), now.AddHours(13), 80m);
        await PublishAsync(BerlinTime.DayOf(now));

        var draftVersions = await VersionsEventuallyAsync(draft.Goal.Goal.Id, 2);
        var confirmedVersions = await VersionsEventuallyAsync(confirmed.Goal.Goal.Id, 2);

        Assert.Equal(PlanStatus.Draft, draftVersions[1].Plan.Status);
        Assert.Equal(PlanReason.PricesPublished, draftVersions[1].Reason);
        Assert.Equal(PlanStatus.Confirmed, confirmedVersions[1].Plan.Status);
        Assert.Equal(PlanStatus.Superseded, confirmedVersions[0].Plan.Status);
        Assert.Single(await Plans.ListVersionsAsync(expired.Goal.Goal.Id, Ct));
    }

    [Fact]
    public async Task The_same_message_twice_adds_no_second_version()
    {
        var now = DateTimeOffset.UtcNow;
        await GoalSeeds.PricesAsync(_database, now, now.AddHours(4), 300m);
        var goal = await CreateGoalAsync("a", now.AddHours(13));
        await GoalSeeds.PricesAsync(_database, now.AddHours(4), now.AddHours(13), 80m);
        var message = Message(BerlinTime.DayOf(now));

        await _worker.Services.GetRequiredService<IMessagePublisher>().PublishAsync(message, Ct);
        await VersionsEventuallyAsync(goal.Goal.Goal.Id, 2);
        await _worker.Services.GetRequiredService<IMessagePublisher>().PublishAsync(message, Ct);
        await WorkerHost.EventuallyAsync(
            () => Task.FromResult(_logs.Lines.Count(l => l.Contains("open goals checked", StringComparison.Ordinal))),
            handled => handled >= 2,
            TimeSpan.FromSeconds(15));

        Assert.Equal(2, (await Plans.ListVersionsAsync(goal.Goal.Goal.Id, Ct)).Count);
    }

    [Fact]
    public async Task A_database_outage_pauses_the_planner_without_spending_retries()
    {
        // A MongoDB of its own, on a fixed port so it comes back at the same address after a stop.
        var port = FreePort();
        await using var own = MongoFixture.NewContainerBuilder().WithPortBinding(port, 27017).Build();
        await own.StartAsync(Ct);
        var settings = MongoClientSettings.FromConnectionString(own.GetConnectionString());
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
        settings.SocketTimeout = TimeSpan.FromSeconds(3);
        var database = new MongoClient(settings).GetDatabase("planner-outage");
        await MongoIndexes.EnsureAsync(database, Ct);

        var now = DateTimeOffset.UtcNow;
        var (vehicle, charger) = await GoalSeeds.CarAndWallboxAsync(database, Owner, "outage");
        await GoalSeeds.PricesAsync(database, now, now.AddHours(4), 300m);
        var planning = NewPlanning(database);
        var goal = Assert.IsType<GoalOutcome.Planned>(await planning.CreateAsync(Owner, new GoalRequest(vehicle, charger, 80, now.AddHours(13)), Ct));
        await GoalSeeds.PricesAsync(database, now.AddHours(4), now.AddHours(13), 80m);

        await using var harness = await ConsumerHarness.StartAsync(rabbit.ConnectionString, nameof(PlannerConsumerTests));
        using var stop = new CancellationTokenSource();
        var handler = new PricesPublishedHandler(
            new GoalStore(database), planning, Options.Create(new PricesOptions()), Options.Create(new PlannerOptions()), harness.Topology, TimeProvider.System, NullLogger<PricesPublishedHandler>.Instance);
        var consumer = new BatchingConsumer<PricesPublished>(
            harness.Connection, harness.Publisher, harness.Topology.Planner, handler, PricesPublished.CurrentSchemaVersion,
            Options.Create(harness.Options), TimeProvider.System, NullLogger.Instance);
        var run = Task.Run(() => consumer.RunAsync(stop.Token), Ct);

        await own.StopAsync(Ct);
        await harness.Publisher.PublishAsync(Message(BerlinTime.DayOf(now)), Ct);
        await Task.Delay(TimeSpan.FromSeconds(8), Ct);

        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Planner.DeadLetter));
        foreach (var retry in harness.Topology.Planner.Retries)
        {
            Assert.Equal(0u, await harness.CountAsync(retry));
        }

        await own.StartAsync(Ct);
        var versions = await WorkerHost.EventuallyAsync(
            async () =>
            {
                try
                {
                    return (await new PlanStore(database).ListVersionsAsync(goal.Goal.Goal.Id, Ct)).Count;
                }
                catch (Exception ex) when (ex is TimeoutException or MongoConnectionException)
                {
                    return 0;
                }
            },
            count => count == 2,
            TimeSpan.FromSeconds(60));
        Assert.Equal(2, versions);
        Assert.Equal(0u, await harness.CountAsync(harness.Topology.Planner.DeadLetter));

        await stop.CancelAsync();
        await run;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static PricesPublished Message(DateOnly day) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, traceparent: null, "DE-LU", day, BerlinTime.SlotCount(day));

    private static GoalPlanningService NewPlanning(IMongoDatabase database) => new(
        new GoalStore(database),
        new PlanStore(database),
        new PriceStore(database),
        new DeviceReader(database),
        new CommandStore(database),
        Options.Create(new PlannerOptions()),
        Options.Create(new PricesOptions()),
        TimeProvider.System,
        NullLogger<GoalPlanningService>.Instance);

    private async Task<GoalOutcome.Planned> CreateGoalAsync(string suffix, DateTimeOffset departure)
    {
        var (vehicle, charger) = await GoalSeeds.CarAndWallboxAsync(_database, Owner, suffix);
        return Assert.IsType<GoalOutcome.Planned>(await Planning.CreateAsync(Owner, new GoalRequest(vehicle, charger, 80, departure), Ct));
    }

    private Task PublishAsync(DateOnly day) => _worker.Services.GetRequiredService<IMessagePublisher>().PublishAsync(Message(day), Ct);

    private Task<IReadOnlyList<StoredPlan>> VersionsEventuallyAsync(GoalId goal, int count) =>
        WorkerHost.EventuallyAsync(() => Plans.ListVersionsAsync(goal, Ct), versions => versions.Count >= count, TimeSpan.FromSeconds(15));
}
