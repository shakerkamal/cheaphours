using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Goals;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>GET /v1/goals</c> against the API role on real MongoDB (planning-api spec). Only the paging test seeds goals
/// for <c>own_b</c>, so it can count them; the other tests use <c>own_a</c> and look for their own goals.
/// </summary>
public sealed class GoalListEndpointTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");
    private static readonly OwnerId OwnerB = OwnerId.Parse("own_b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Goals_are_listed_newest_first_across_pages()
    {
        // Ids made one millisecond apart sort in creation order, as the API's UUIDv7 ids do.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var made = new List<GoalId>();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            made.Add(await PlanSeeds.GoalAsync(shared.Database, OwnerB, DateTimeOffset.UtcNow.AddHours(12), 1m, 2m, goalId: GoalId.New(clock)));
        }

        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        var first = await GetAsync(client, "/v1/goals?limit=2");
        Assert.Equal([made[2].Value, made[1].Value], Ids(first));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await GetAsync(client, $"/v1/goals?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal([made[0].Value], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Another_owners_goal_is_never_listed()
    {
        var theirs = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), 1m, 2m);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        var listed = await ListAllAsync(client);

        Assert.DoesNotContain(listed, g => g.GetProperty("id").GetString() == theirs.Value);
    }

    [Fact]
    public async Task Confirmed_goal_has_no_expiry_and_a_draft_past_its_expiry_reads_expired()
    {
        var confirmed = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), 1m, 2m, PlanStatus.Confirmed);
        var expired = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), 1m, 2m);
        var plan = await new PlanStore(shared.Database).GetCurrentAsync(expired, Ct);
        await GoalSeeds.ExpireAsync(shared.Database, plan!.VersionId);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var listed = await ListAllAsync(client);

        var first = Single(listed, confirmed).GetProperty("currentPlan");
        Assert.Equal("Confirmed", first.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("expiresAt").ValueKind);
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        var second = Single(listed, expired).GetProperty("currentPlan");
        Assert.Equal("Expired", second.GetProperty("status").GetString());
        Assert.Equal(plan.Plan.Id.Value, second.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Change_draft_above_a_confirmed_version_is_listed_as_the_plan_endpoint_shows_it()
    {
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, DateTimeOffset.UtcNow.AddHours(12), 1m, 2m, PlanStatus.Confirmed);
        var running = (await new PlanStore(shared.Database).GetCurrentAsync(goal, Ct))!;
        var now = DateTimeOffset.UtcNow;
        var draft = new StoredPlan(
            new Plan(running.Plan.Id, goal, OwnerA, 2, PlanStatus.Draft, running.Plan.Slots, 0.9m, 2m, targetMissed: false, pricesComplete: true, now, now.AddMinutes(10)),
            PlanReason.GoalChanged,
            running.StartSocPercent,
            90,
            running.EnergyNeededWh,
            ProposedTargetSocPercent: 90);
        Assert.Equal(GoalWriteOutcome.Stored, await new GoalStore(shared.Database).AddChangeDraftAsync(draft, Ct));
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var listed = Single(await ListAllAsync(client), goal);
        var shown = await GetAsync(client, $"/v1/goals/{goal}/plan");

        var current = listed.GetProperty("currentPlan");
        Assert.Equal("Draft", current.GetProperty("status").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal(shown.GetProperty("version").GetInt32(), current.GetProperty("version").GetInt32());
        Assert.Equal(shown.GetProperty("status").GetString(), current.GetProperty("status").GetString());
        Assert.Equal(shown.GetProperty("expiresAt").GetDateTimeOffset(), current.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(80, listed.GetProperty("targetSocPercent").GetInt32());
    }

    private static string?[] Ids(JsonElement page) =>
        [.. page.GetProperty("data").EnumerateArray().Select(g => g.GetProperty("id").GetString())];

    private static JsonElement Single(IEnumerable<JsonElement> goals, GoalId id) =>
        Assert.Single(goals, g => g.GetProperty("id").GetString() == id.Value);

    private static async Task<List<JsonElement>> ListAllAsync(HttpClient client)
    {
        var goals = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await GetAsync(client, cursor is null ? "/v1/goals?limit=200" : $"/v1/goals?limit=200&cursor={Uri.EscapeDataString(cursor)}");
            goals.AddRange(page.GetProperty("data").EnumerateArray().Select(g => g.Clone()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return goals;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
