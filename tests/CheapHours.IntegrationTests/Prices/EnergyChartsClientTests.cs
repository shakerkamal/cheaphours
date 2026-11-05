using CheapHours.Host.Prices;
using CheapHours.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static CheapHours.IntegrationTests.Prices.EnergyChartsFake;

namespace CheapHours.IntegrationTests.Prices;

/// <summary>The Energy-Charts client against a WireMock stand-in (design §2).</summary>
public sealed class EnergyChartsClientTests : IDisposable
{
    private const string Marker = "BODY-MARKER-7f3a";
    private static readonly DateOnly Day = new(2026, 11, 2);

    private readonly EnergyChartsFake _source = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ServiceProvider _services;

    public EnergyChartsClientTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Prices:BaseUrl"] = _source.BaseUrl.ToString(),
            ["Prices:AttemptTimeout"] = "00:00:00.500",
            ["Prices:TotalTimeout"] = "00:00:03",
            ["Prices:RetryBaseDelay"] = "00:00:00.010",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(l => l.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        services.AddPriceSource();
        _services = services.BuildServiceProvider();
    }

    private IPriceSource Client => _services.GetRequiredService<IPriceSource>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ok_with_a_recorded_day_is_published()
    {
        _source.Script(Day, Answer.Ok(Fixture(Day)));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        var published = Assert.IsType<PriceFetch.Published>(fetch);
        Assert.Equal(96, published.Day.Slots.Count);
        Assert.True(published.Day.IsComplete);
        Assert.False(published.Deprecated);
        Assert.Equal(["2026-11-02"], _source.Requests);
    }

    [Fact]
    public async Task Not_found_is_not_published()
    {
        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        Assert.IsType<PriceFetch.NotPublished>(fetch);
        Assert.Single(_source.Requests);
    }

    [Fact]
    public async Task Too_many_requests_is_rate_limited_after_exactly_one_request()
    {
        _source.Script(Day, new Answer(429, "Too Many Requests " + Marker));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        Assert.IsType<PriceFetch.RateLimited>(fetch);
        Assert.Single(_source.Requests);
    }

    [Fact]
    public async Task Server_error_is_retried_then_failed()
    {
        _source.Script(Day, new Answer(503, "down " + Marker));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        Assert.Equal(new PriceFetch.Failed("503"), fetch);
        Assert.Equal(4, _source.Requests.Count); // the first attempt and three retries
    }

    [Fact]
    public async Task Server_error_then_ok_succeeds_within_the_run()
    {
        _source.Script(Day, new Answer(502, "bad gateway"), Answer.Ok(Fixture(Day)));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        Assert.IsType<PriceFetch.Published>(fetch);
        Assert.Equal(2, _source.Requests.Count);
    }

    [Fact]
    public async Task Timeout_fails()
    {
        _source.Script(Day, new Answer(200, Fixture(Day), Delay: TimeSpan.FromSeconds(2)));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        var failed = Assert.IsType<PriceFetch.Failed>(fetch);
        Assert.Contains("Timeout", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_breaking_a_rule_is_rejected()
    {
        _source.Script(Day, Answer.Ok(Fixture(Day).Replace("EUR / MWh", "EUR / kWh", StringComparison.Ordinal)));

        var fetch = await Client.FetchAsync("DE-LU", Day, Ct);

        Assert.Equal(new PriceFetch.Rejected(PriceRule.Unit), fetch);
    }

    [Fact]
    public async Task No_body_reaches_any_log_line()
    {
        var other = Day.AddDays(1);
        var unreadable = Day.AddDays(2);
        _source.Script(Day, new Answer(503, "down " + Marker));
        _source.Script(other, Answer.Ok(Fixture(Day).Replace("CC BY 4.0", Marker, StringComparison.Ordinal)));
        _source.Script(unreadable, Answer.Ok("{ not json " + Marker));

        await Client.FetchAsync("DE-LU", Day, Ct);
        await Client.FetchAsync("DE-LU", other, Ct);
        await Client.FetchAsync("DE-LU", unreadable, Ct);

        Assert.NotEmpty(_logs.Lines);
        Assert.False(_logs.Contains(Marker), string.Join('\n', _logs.Lines.Where(l => l.Contains(Marker, StringComparison.Ordinal))));
    }

    public void Dispose()
    {
        _services.Dispose();
        _source.Dispose();
        _logs.Dispose();
    }
}
