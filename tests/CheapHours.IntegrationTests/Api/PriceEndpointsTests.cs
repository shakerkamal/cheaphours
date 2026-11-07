using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core.Prices;
using CheapHours.Persistence.Prices;

namespace CheapHours.IntegrationTests.Api;

/// <summary><c>GET /v1/prices</c> against the API role on real MongoDB (planning-api spec).</summary>
public sealed class PriceEndpointsTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_complete_day_comes_in_time_order_on_one_page()
    {
        var dayStart = new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero);
        await SeedDayAsync(dayStart);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var page = await GetPageAsync(client, "zone=DE-LU&from=2026-11-01T23:00:00Z&to=2026-11-02T23:00:00Z&limit=200");

        var slots = page.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(96, slots.Count);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal(dayStart, slots[0].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(dayStart.AddMinutes(15), slots[0].GetProperty("end").GetDateTimeOffset());
        Assert.Equal("DE-LU", slots[0].GetProperty("zone").GetString());
        Assert.Equal(-1.5m, slots[0].GetProperty("priceEurPerMwh").GetDecimal());
        Assert.Equal(slots.Select(s => s.GetProperty("start").GetDateTimeOffset()).Order(), slots.Select(s => s.GetProperty("start").GetDateTimeOffset()));
    }

    [Fact]
    public async Task Fifty_then_the_remaining_forty_six()
    {
        await SeedDayAsync(new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero));
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        const string range = "zone=DE-LU&from=2026-10-19T22:00:00Z&to=2026-10-20T22:00:00Z";

        var first = await GetPageAsync(client, range + "&limit=50");
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await GetPageAsync(client, $"{range}&limit=50&cursor={cursor}");

        Assert.Equal(50, first.GetProperty("data").GetArrayLength());
        Assert.Equal(46, second.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(
            first.GetProperty("data")[49].GetProperty("start").GetDateTimeOffset().AddMinutes(15),
            second.GetProperty("data")[0].GetProperty("start").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("zone=FR&from=2026-11-01T23:00:00Z&to=2026-11-02T23:00:00Z", "zone")]
    [InlineData("zone=DE-LU&from=2026-09-01T00:00:00Z&to=2026-10-11T00:00:00Z", "to")]
    [InlineData("zone=DE-LU&from=2026-11-01T23:00:00Z&to=2026-11-02T23:00:00Z&limit=201", "limit")]
    [InlineData("zone=DE-LU&from=2026-11-01T23:00:00Z&to=2026-11-02T23:00:00Z&cursor=not-a-cursor", "cursor")]
    public async Task Invalid_query_gets_400_keyed_by_the_parameter(string query, string parameter)
    {
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await client.GetAsync(new Uri("/v1/prices?" + query, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.EndsWith("/validation", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), problem.ToString());
    }

    private static async Task<JsonElement> GetPageAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/v1/prices?" + query, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<PriceWriteResult> SeedDayAsync(DateTimeOffset dayStart) =>
        new PriceStore(shared.Database).UpsertAsync(
            [.. Enumerable.Range(0, 96).Select(i => new PriceSlot("DE-LU", dayStart.AddMinutes(15 * i), i - 1.5m))],
            dayStart,
            Ct);
}
