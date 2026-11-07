using System.Collections.Concurrent;
using CheapHours.Host.Prices;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.IntegrationTests.Worker;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Locks;
using CheapHours.Persistence.Prices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using static CheapHours.IntegrationTests.Prices.EnergyChartsFake;

namespace CheapHours.IntegrationTests.Prices;

/// <summary>The price import job against WireMock, MongoDB and RabbitMQ (price-import spec, design §8).</summary>
public sealed class PriceImportJobTests(MongoFixture mongo, RabbitMqFixture rabbit) : IAsyncLifetime
{
    // One database and broker prefix for the class; each test imports for its own zone.
    private static readonly string Database = MongoFixture.NewDatabaseName(nameof(PriceImportJobTests));
    private static readonly string Prefix = RabbitMqFixture.NewPrefix(nameof(PriceImportJobTests));
    private static readonly DateOnly Recorded = new(2026, 11, 2);

    private readonly EnergyChartsFake _source = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly string _zone = "Z" + SharedDatabases.NewSuffix();
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await WorkerHost.StopAsync(host);
        }

        _source.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Not_published_then_published_announces_once_with_the_deterministic_id()
    {
        // 13:00 Berlin on 1 November: tomorrow is the recorded 2 November.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        _source.Script(Recorded, Answer.NotPublished, Answer.Ok(Fixture(Recorded)));
        var (job, publisher, host) = await StartJobAsync("replica-a", clock);

        Assert.True(await job.RunOnceAsync(Ct));
        Assert.Empty(publisher.Published);

        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        var announced = Assert.Single(publisher.Published);
        Assert.Equal(PriceImportJob.AnnouncementId(_zone, Recorded), announced.MessageId);
        Assert.Equal(Recorded, announced.DeliveryDay);
        Assert.Equal(96, announced.SlotCount);
        Assert.Equal(_zone, announced.Zone);
        var day = await Days(host).GetAsync(_zone, Recorded, Ct);
        Assert.True(day!.IsComplete);
        Assert.NotNull(day.AnnouncedAt);

        // A later run fetches nothing more for the complete day and announces nothing.
        clock.Advance(TimeSpan.FromHours(1));
        var requestsBefore = _source.Requests.Count(r => r == "2026-11-02");
        await job.RunOnceAsync(Ct);
        Assert.Single(publisher.Published);
        Assert.Equal(requestsBefore, _source.Requests.Count(r => r == "2026-11-02"));
    }

    [Fact]
    public async Task Broker_down_at_completion_announces_at_the_next_run_with_the_same_id()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        _source.Script(Recorded, Answer.Ok(Fixture(Recorded)));
        var (job, publisher, host) = await StartJobAsync("replica-a", clock, failFirstPublish: true);

        await job.RunOnceAsync(Ct);

        Assert.Empty(publisher.Published);
        Assert.Equal(1, publisher.Failures);
        var day = await Days(host).GetAsync(_zone, Recorded, Ct);
        Assert.True(day!.IsComplete);
        Assert.Null(day.AnnouncedAt);
        Assert.True(_logs.Contains("broker unavailable"));

        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        var announced = Assert.Single(publisher.Published);
        Assert.Equal(PriceImportJob.AnnouncementId(_zone, Recorded), announced.MessageId);
        Assert.NotNull((await Days(host).GetAsync(_zone, Recorded, Ct))!.AnnouncedAt);
    }

    [Fact]
    public async Task Prices_never_arriving_give_one_warning_and_no_request_after_eight_pm()
    {
        // Today is the recorded day (complete after the first run); tomorrow, 3 November, never arrives.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero)); // 19:30 Berlin
        _source.Script(Recorded, Answer.Ok(Fixture(Recorded)));
        var (job, _, host) = await StartJobAsync("replica-a", clock);

        await job.RunOnceAsync(Ct);
        Assert.Equal(["2026-11-02", "2026-11-03"], _source.Requests);

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 2, 19, 0, 0, TimeSpan.Zero)); // 20:00 Berlin
        await job.RunOnceAsync(Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 2, 22, 55, 0, TimeSpan.Zero)); // 23:55 Berlin
        await job.RunOnceAsync(Ct);

        Assert.Equal(["2026-11-02", "2026-11-03"], _source.Requests);
        var warning = Assert.Single(_logs.Lines, l => l.Contains("Gave up on prices", StringComparison.Ordinal));
        Assert.Contains(_zone, warning, StringComparison.Ordinal);
        Assert.True(warning.Contains("11/03/2026", StringComparison.Ordinal) || warning.Contains("2026-11-03", StringComparison.Ordinal), warning);
        Assert.NotNull((await Days(host).GetAsync(_zone, new DateOnly(2026, 11, 3), Ct))!.GaveUpAt);
    }

    [Fact]
    public async Task Worker_started_after_eight_pm_tries_tomorrow_once_and_announces_it()
    {
        // 21:00 Berlin on 1 November; tomorrow, the recorded 2 November, has been published since 13:00.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero));
        _source.Script(Recorded, Answer.Ok(Fixture(Recorded)));
        var (job, publisher, host) = await StartJobAsync("replica-a", clock);

        await job.RunOnceAsync(Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        Assert.Single(_source.Requests, r => r == "2026-11-02");
        var day = await Days(host).GetAsync(_zone, Recorded, Ct);
        Assert.True(day!.IsComplete);
        Assert.Null(day.GaveUpAt);
        Assert.Equal(PriceImportJob.AnnouncementId(_zone, Recorded), Assert.Single(publisher.Published).MessageId);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("Gave up on prices", StringComparison.Ordinal) && l.Contains(_zone, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_started_after_eight_pm_gives_up_after_one_try()
    {
        // 21:00 Berlin on 2 November: today is recorded, tomorrow (3 November) is not published.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero));
        _source.Script(Recorded, Answer.Ok(Fixture(Recorded)));
        var (job, _, host) = await StartJobAsync("replica-a", clock);

        await job.RunOnceAsync(Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        Assert.Equal(["2026-11-02", "2026-11-03"], _source.Requests);
        var warning = Assert.Single(_logs.Lines, l => l.Contains("Gave up on prices", StringComparison.Ordinal) && l.Contains(_zone, StringComparison.Ordinal));
        Assert.True(warning.Contains("11/03/2026", StringComparison.Ordinal) || warning.Contains("2026-11-03", StringComparison.Ordinal), warning);
        var tomorrow = await Days(host).GetAsync(_zone, new DateOnly(2026, 11, 3), Ct);
        Assert.NotNull(tomorrow!.AttemptedAt);
        Assert.NotNull(tomorrow.GaveUpAt);
    }

    [Fact]
    public async Task Rate_limit_ends_the_run_and_the_next_run_fetches_again()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)); // 13:00 Berlin
        _source.Script(Recorded, new Answer(429, "Too Many Requests"), Answer.Ok(Fixture(Recorded)));
        var (job, _, host) = await StartJobAsync("replica-a", clock);

        await job.RunOnceAsync(Ct);

        Assert.Equal(["2026-11-02"], _source.Requests); // tomorrow was not tried in the same run
        Assert.Null((await Days(host).GetAsync(_zone, Recorded, Ct))?.CompleteAt);
        Assert.Equal(0, await host.Services.GetRequiredService<IPriceStore>().CountAsync(_zone, BerlinTime.DayStartUtc(Recorded), BerlinTime.DayEndUtc(Recorded), Ct));

        clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        Assert.True((await Days(host).GetAsync(_zone, Recorded, Ct))!.IsComplete);
    }

    [Fact]
    public async Task Two_workers_make_one_source_call_per_run()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)); // 09:00 Berlin
        _source.Script(Recorded, new Answer(200, Fixture(Recorded), Delay: TimeSpan.FromMilliseconds(500)));
        var (a, _, _) = await StartJobAsync("replica-a", clock);
        var (b, _, _) = await StartJobAsync("replica-b", clock);

        var ran = await Task.WhenAll(a.RunOnceAsync(Ct), b.RunOnceAsync(Ct));

        Assert.Single(ran, r => r);
        Assert.Equal(["2026-11-02"], _source.Requests);
    }

    private static IPriceDayStore Days(IHost host) => host.Services.GetRequiredService<IPriceDayStore>();

    private async Task<(PriceImportJob Job, RecordingPublisher Publisher, IHost Host)> StartJobAsync(
        string replica,
        TimeProvider clock,
        bool failFirstPublish = false)
    {
        var host = await WorkerHost.StartAsync(mongo, rabbit, Database, Prefix, new Dictionary<string, string>
        {
            ["Worker:ReplicaId"] = replica,
            ["Prices:Zone"] = _zone,
            ["Prices:BaseUrl"] = _source.BaseUrl.ToString(),
            ["Prices:PauseBetweenRequests"] = "00:00:00",
            ["Prices:RetryBaseDelay"] = "00:00:00.010",
        }, _logs);
        _hosts.Add(host);

        var publisher = new RecordingPublisher(host.Services.GetRequiredService<IMessagePublisher>(), failFirstPublish);
        var job = ActivatorUtilities.CreateInstance<PriceImportJob>(
            host.Services,
            host.Services.GetRequiredKeyedService<ILease>(LeaseNames.PriceImport),
            publisher,
            clock);
        return (job, publisher, host);
    }

    /// <summary>Forwards to the real publisher and keeps what the broker confirmed; can fail the first publish.</summary>
    private sealed class RecordingPublisher(IMessagePublisher inner, bool failFirst) : IMessagePublisher
    {
        private readonly ConcurrentQueue<PricesPublished> _published = new();
        private int _failures;

        public IReadOnlyList<PricesPublished> Published => [.. _published];

        public int Failures => _failures;

        public async Task PublishAsync(Message message, CancellationToken cancellationToken)
        {
            if (failFirst && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
            {
                throw new BrokerUnavailableException("broker:5672");
            }

            await inner.PublishAsync(message, cancellationToken);
            if (message is PricesPublished prices)
            {
                _published.Enqueue(prices);
            }
        }

        public Task SendToQueueAsync(Message message, string queue, CancellationToken cancellationToken) =>
            inner.SendToQueueAsync(message, queue, cancellationToken);

        public Task PublishSerializedAsync(SerializedMessage message, string destination, bool toQueue, CancellationToken cancellationToken) =>
            inner.PublishSerializedAsync(message, destination, toQueue, cancellationToken);
    }
}
