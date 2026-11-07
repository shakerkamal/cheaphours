using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.IntegrationTests.Api;

/// <summary><c>GET /v1/goals/{id}/plan</c> against the API role on real MongoDB (planning-api spec).</summary>
public sealed class PlanEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Draft_past_its_expiry_reads_as_expired()
    {
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), expected: 1m, arrival: 2m);
        var plan = await new PlanStore(shared.Database).GetCurrentAsync(goal, Ct);
        await GoalSeeds.ExpireAsync(shared.Database, plan!.VersionId);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var body = await GetPlanAsync(client, goal);

        Assert.Equal("Expired", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task Costs_are_shown_to_the_cent_and_savings_add_up()
    {
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), expected: 0.495m, arrival: 1.595m);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var body = await GetPlanAsync(client, goal);

        Assert.Equal(0.50m, body.GetProperty("expectedCostEur").GetDecimal());
        Assert.Equal(1.60m, body.GetProperty("arrivalCostEur").GetDecimal());
        Assert.Equal(1.10m, body.GetProperty("savingsEur").GetDecimal());
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("pricesComplete").GetBoolean());
        var slot = body.GetProperty("slots")[0];
        Assert.Equal(slot.GetProperty("start").GetDateTimeOffset().AddMinutes(15), slot.GetProperty("end").GetDateTimeOffset());
        Assert.Equal(11_000, slot.GetProperty("powerW").GetInt32());
    }

    [Fact]
    public async Task Another_owners_goal_gets_404()
    {
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), expected: 1m, arrival: 2m);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        using var response = await client.GetAsync(new Uri($"/v1/goals/{goal}/plan", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.EndsWith("/not-found", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_goal_gets_404()
    {
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await client.GetAsync(new Uri("/v1/goals/goal_nothing_here/plan", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> GetPlanAsync(HttpClient client, GoalId goal)
    {
        using var response = await client.GetAsync(new Uri($"/v1/goals/{goal}/plan", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
