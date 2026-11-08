using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Plans;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>POST /v1/plans/{id}/confirm</c> against the API role on real MongoDB (planning-api spec: confirming a plan returns
/// its commands; plans that cannot be confirmed are refused; a repeated confirmation returns the first response).
/// Each test seeds its own goal, so the tests share one host and database.
/// </summary>
public sealed class PlanConfirmEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // planning-api: "Draft with two runs".
    [Fact]
    public async Task Draft_with_two_runs_gets_202_with_the_confirmed_plan_and_four_pending_commands()
    {
        var departure = SlotGrid.Floor(DateTimeOffset.UtcNow).AddHours(13);
        var first = departure.AddHours(-6);
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, departure, 1m, 2m, slotStarts: [first, first.AddMinutes(15), first.AddHours(2)]);
        var planId = await PlanIdAsync(goal);
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/v1/goals/{goal}/plan", response.Headers.Location!.OriginalString);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Confirmed", body.GetProperty("plan").GetProperty("status").GetString());
        var commands = body.GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(4, commands.Length);
        Assert.All(commands, c => Assert.Equal("Pending", c.GetProperty("state").GetString()));
        Assert.All(commands, c => Assert.StartsWith("cmd_", c.GetProperty("id").GetString(), StringComparison.Ordinal));
        Assert.Equal(4, (await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct)).Count);
    }

    // planning-api: "Read key".
    [Fact]
    public async Task Read_key_gets_403()
    {
        var planId = await PlanIdAsync(await PlanSeeds.GoalAsync(shared.Database, OwnerA, Departure(), 1m, 2m));
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct));
    }

    [Fact]
    public async Task Another_owners_plan_gets_404()
    {
        var planId = await PlanIdAsync(await PlanSeeds.GoalAsync(shared.Database, OwnerId.Parse("own_b"), Departure(), 1m, 2m));
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
        Assert.Empty(await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct));
    }

    // planning-api: "Confirmed twice with different keys".
    [Fact]
    public async Task Second_confirmation_with_a_new_key_gets_409_and_adds_no_command()
    {
        var planId = await PlanIdAsync(await PlanSeeds.GoalAsync(shared.Database, OwnerA, Departure(), 1m, 2m));
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);
        using var first = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        using var second = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "conflict");
        Assert.Equal(2, (await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct)).Count);
    }

    // planning-api: "Draft expired".
    [Fact]
    public async Task Draft_confirmed_eleven_minutes_after_it_was_made_gets_410()
    {
        var made = DateTimeOffset.UtcNow.AddMinutes(-11);
        var planId = await PlanIdAsync(await PlanSeeds.GoalAsync(shared.Database, OwnerA, Departure(), 1m, 2m, madeAt: made));
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);

        using var response = await ConfirmAsync(client, planId, Guid.NewGuid().ToString());

        await AssertProblemAsync(response, HttpStatusCode.Gone, "plan-expired");
        Assert.Empty(await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct));
    }

    // planning-api: "Client retries the confirmation".
    [Fact]
    public async Task Same_key_twice_returns_the_first_response_with_the_same_command_ids()
    {
        var planId = await PlanIdAsync(await PlanSeeds.GoalAsync(shared.Database, OwnerA, Departure(), 1m, 2m));
        using var client = shared.Api.CreateClient(ApiFactory.ExecuteKeyA);
        var key = Guid.NewGuid().ToString();

        using var first = await ConfirmAsync(client, planId, key);
        using var second = await ConfirmAsync(client, planId, key);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, second.StatusCode));
        Assert.Equal(await CommandIdsAsync(first), await CommandIdsAsync(second));
        Assert.Equal(2, (await new CommandStore(shared.Database).ListForPlanAsync(planId, Ct)).Count);
    }

    private static DateTimeOffset Departure() => SlotGrid.Floor(DateTimeOffset.UtcNow).AddHours(13);

    private static async Task<string[]> CommandIdsAsync(HttpResponseMessage response) =>
        [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("id").GetString()!)];

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, PlanId planId, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v1/plans/{planId}/confirm", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request, Ct);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.EndsWith("/" + type, JsonDocument.Parse(body).RootElement.GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    private async Task<PlanId> PlanIdAsync(GoalId goal) => (await new PlanStore(shared.Database).GetCurrentAsync(goal, Ct))!.Plan.Id;
}
