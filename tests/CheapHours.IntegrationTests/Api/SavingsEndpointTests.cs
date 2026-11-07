using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>GET /v1/savings</c> against the API role on real MongoDB (planning-api spec). Confirmed versions are seeded
/// through the stores until plan confirmation exists. Each test uses its own departure range.
/// </summary>
public sealed class SavingsEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_confirmed_sessions_are_summed()
    {
        var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await Seed(day.AddHours(6), 1.20m, 2.00m, PlanStatus.Confirmed);
        await Seed(day.AddHours(18), 0.80m, 1.50m, PlanStatus.Confirmed);
        await Seed(day.AddDays(2), 0.10m, 9.00m, PlanStatus.Confirmed); // departs outside the range

        var savings = await GetAsync("from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z");

        Assert.Equal(2, savings.GetProperty("sessions").GetInt32());
        Assert.Equal(2.00m, savings.GetProperty("plannedCostEur").GetDecimal());
        Assert.Equal(3.50m, savings.GetProperty("arrivalCostEur").GetDecimal());
        Assert.Equal(1.50m, savings.GetProperty("savingsEur").GetDecimal());
    }

    [Fact]
    public async Task Drafts_only_give_zeros()
    {
        var day = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        await Seed(day.AddHours(6), 1.20m, 2.00m, PlanStatus.Draft);

        var savings = await GetAsync("from=2026-09-05T00:00:00Z&to=2026-09-06T00:00:00Z");

        Assert.Equal(0, savings.GetProperty("sessions").GetInt32());
        Assert.Equal(0m, savings.GetProperty("plannedCostEur").GetDecimal());
        Assert.Equal(0m, savings.GetProperty("arrivalCostEur").GetDecimal());
        Assert.Equal(0m, savings.GetProperty("savingsEur").GetDecimal());
    }

    [Fact]
    public async Task A_version_with_incomplete_prices_is_left_out()
    {
        var day = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        await Seed(day.AddHours(6), 1.00m, 2.00m, PlanStatus.Confirmed);
        await Seed(day.AddHours(7), 0.50m, 3.00m, PlanStatus.Confirmed, pricesComplete: false);

        var savings = await GetAsync("from=2026-09-08T00:00:00Z&to=2026-09-09T00:00:00Z");

        Assert.Equal(1, savings.GetProperty("sessions").GetInt32());
        Assert.Equal(1.00m, savings.GetProperty("savingsEur").GetDecimal());
    }

    [Theory]
    [InlineData("from=2026-09-01T00:00:00Z&to=2026-10-11T00:00:00Z", "to")]
    [InlineData("from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", "to")]
    [InlineData("from=2026-09-01T00:00:00%2B02:00&to=2026-09-02T00:00:00Z", "from")]
    public async Task Invalid_range_gets_400(string query, string parameter)
    {
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await client.GetAsync(new Uri("/v1/savings?" + query, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), problem.ToString());
    }

    private Task<GoalId> Seed(DateTimeOffset departure, decimal expected, decimal arrival, PlanStatus status, bool pricesComplete = true) =>
        PlanSeeds.GoalAsync(shared.Database, OwnerA, departure, expected, arrival, status, pricesComplete, madeAt: departure.AddHours(-12));

    private async Task<JsonElement> GetAsync(string query)
    {
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        using var response = await client.GetAsync(new Uri("/v1/savings?" + query, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
