using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheapHours.Core;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using CheapHours.IntegrationTests.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>POST /v1/goals</c> and <c>PATCH /v1/goals/{id}</c> against the API role on real MongoDB (planning-api spec).
/// Each test seeds its own vehicle and charger, so the tests share one host and database.
/// </summary>
public sealed class GoalEndpointsTests(SharedApi shared) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");
    private static readonly OwnerId OwnerB = OwnerId.Parse("own_b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Unprocessable => ["past", "too-soon", "too-far", "wrong-type", "same-device", "no-charge", "below-charge"];

    [Fact]
    public async Task Goal_gets_201_with_its_location_and_a_draft_plan()
    {
        var (vehicle, charger) = await DevicesAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PostAsync(client, vehicle, charger, 80, Departure());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var goal = body.GetProperty("goal");
        var plan = body.GetProperty("plan");
        Assert.Equal($"/v1/goals/{goal.GetProperty("id").GetString()}/plan", response.Headers.Location!.OriginalString);
        Assert.Equal(vehicle.Value, goal.GetProperty("vehicleDeviceId").GetString());
        Assert.Equal(80, goal.GetProperty("targetSocPercent").GetInt32());
        Assert.Equal(1, plan.GetProperty("version").GetInt32());
        Assert.Equal("Draft", plan.GetProperty("status").GetString());
        Assert.Equal(10, plan.GetProperty("slots").GetArrayLength());
        Assert.True(plan.GetProperty("expectedCostEur").GetDecimal() > 0);
        Assert.Equal(
            plan.GetProperty("arrivalCostEur").GetDecimal() - plan.GetProperty("expectedCostEur").GetDecimal(),
            plan.GetProperty("savingsEur").GetDecimal());
        Assert.True(plan.GetProperty("pricesComplete").GetBoolean());
        Assert.False(plan.GetProperty("targetMissed").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, plan.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task Target_out_of_range_gets_400_for_the_field()
    {
        var (vehicle, charger) = await DevicesAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PostAsync(client, vehicle, charger, 120, Departure());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", field: "targetSocPercent");
    }

    [Fact]
    public async Task Another_owners_charger_gets_404()
    {
        var (vehicle, _) = await DevicesAsync();
        var (_, foreignCharger) = await GoalSeeds.CarAndWallboxAsync(shared.Database, OwnerB, "b");
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PostAsync(client, vehicle, foreignCharger, 80, Departure());

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    [Theory]
    [MemberData(nameof(Unprocessable))]
    public async Task Goal_that_cannot_be_planned_gets_422_and_stores_nothing(string reason)
    {
        var (vehicle, charger) = await DevicesAsync(soc: reason switch
        {
            "no-charge" => null,
            "below-charge" => 70.0,
            _ => 40.0,
        });
        var now = DateTimeOffset.UtcNow;
        var (v, c, target, departure) = reason switch
        {
            "past" => (vehicle, charger, 80, now.AddHours(-1)),
            "too-soon" => (vehicle, charger, 80, now.AddMinutes(20)),
            "too-far" => (vehicle, charger, 80, now.AddHours(50)),
            "wrong-type" => (charger, vehicle, 80, Departure()),
            "same-device" => (vehicle, vehicle, 80, Departure()),
            "below-charge" => (vehicle, charger, 60, Departure()),
            _ => (vehicle, charger, 80, Departure()),
        };
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PostAsync(client, v, c, target, departure);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "unprocessable");

        // Nothing was stored: the same devices can still get a goal.
        if (reason is not ("no-charge" or "wrong-type" or "same-device"))
        {
            using var retry = await PostAsync(client, vehicle, charger, 80, Departure());
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        }
    }

    [Fact]
    public async Task Second_goal_for_the_vehicle_gets_409_pointing_to_a_change()
    {
        var (vehicle, charger) = await DevicesAsync();
        var (_, otherCharger) = await DevicesAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        using var first = await PostAsync(client, vehicle, charger, 80, Departure());

        using var second = await PostAsync(client, vehicle, otherCharger, 90, Departure().AddHours(1));

        var problem = await AssertProblemAsync(second, HttpStatusCode.Conflict, "conflict");
        Assert.Contains("PATCH /v1/goals/{id}", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_target_gives_a_draft_one_version_higher()
    {
        var goalId = await CreateGoalAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PatchAsync(client, goalId, """{"targetSocPercent":90}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(90, body.GetProperty("goal").GetProperty("targetSocPercent").GetInt32());
        Assert.Equal(2, body.GetProperty("plan").GetProperty("version").GetInt32());
        Assert.Equal("Draft", body.GetProperty("plan").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Empty_change_gets_400()
    {
        var goalId = await CreateGoalAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PatchAsync(client, goalId, "{}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Changing_another_owners_goal_gets_404()
    {
        var goalId = await CreateGoalAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        using var response = await PatchAsync(client, goalId, """{"targetSocPercent":90}""");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    // planning-api: "Confirmed plan" (both scenarios).
    [Fact]
    public async Task Changing_a_goal_with_a_confirmed_plan_gets_200_with_a_change_draft_and_the_goal_unchanged()
    {
        var goalId = await CreateGoalAsync();
        var plans = new PlanStore(shared.Database);
        var plan = await plans.GetCurrentAsync(GoalId.Parse(goalId), Ct);
        using (var execute = shared.Api.CreateClient(ApiFactory.ExecuteKeyA))
        {
            using var confirmed = await execute.PostAsync(new Uri($"/v1/plans/{plan!.Plan.Id}/confirm", UriKind.Relative), null, Ct);
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PatchAsync(client, goalId, """{"targetSocPercent":90}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(80, body.GetProperty("goal").GetProperty("targetSocPercent").GetInt32());
        var draft = body.GetProperty("plan");
        Assert.Equal((2, "Draft", 90), (draft.GetProperty("version").GetInt32(), draft.GetProperty("status").GetString(), draft.GetProperty("proposedTargetSocPercent").GetInt32()));
        Assert.Equal(PlanStatus.Confirmed, (await plans.GetRunningAsync(GoalId.Parse(goalId), Ct))!.Plan.Status);
    }

    // planning-api: "Reading a change draft".
    [Fact]
    public async Task Reading_a_change_draft_shows_its_proposed_departure_and_target()
    {
        var goalId = await CreateGoalAsync();
        var plan = await new PlanStore(shared.Database).GetCurrentAsync(GoalId.Parse(goalId), Ct);
        using (var execute = shared.Api.CreateClient(ApiFactory.ExecuteKeyA))
        {
            using var confirmed = await execute.PostAsync(new Uri($"/v1/plans/{plan!.Plan.Id}/confirm", UriKind.Relative), null, Ct);
        }

        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        var later = Departure().AddHours(1);
        using var changed = await PatchAsync(client, goalId, $$"""{"departureAt":"{{later.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}"}""");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var read = await client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/goals/{goalId}/plan", UriKind.Relative), Ct);

        Assert.Equal(later, read.GetProperty("proposedDepartureAt").GetDateTimeOffset());
        Assert.Equal(80, read.GetProperty("proposedTargetSocPercent").GetInt32());
    }

    [Fact]
    public async Task Draft_plan_shows_no_proposed_values()
    {
        var goalId = await CreateGoalAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        var read = await client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/goals/{goalId}/plan", UriKind.Relative), Ct);

        Assert.Equal(JsonValueKind.Null, read.GetProperty("proposedTargetSocPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("proposedDepartureAt").ValueKind);
    }

    [Fact]
    public async Task Changing_a_goal_whose_departure_passed_gets_409()
    {
        var goal = await PlanSeeds.GoalAsync(shared.Database, OwnerA, departure: DateTimeOffset.UtcNow.AddDays(-1), expected: 1m, arrival: 2m, madeAt: DateTimeOffset.UtcNow.AddDays(-2));
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);

        using var response = await PatchAsync(client, goal.Value, """{"targetSocPercent":90}""");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "conflict");
    }

    private static DateTimeOffset Departure() => SlotGrid.Floor(DateTimeOffset.UtcNow).AddHours(13);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, DeviceId vehicle, DeviceId charger, int target, DateTimeOffset departure) =>
        client.PostAsJsonAsync(new Uri("/v1/goals", UriKind.Relative), new
        {
            vehicleDeviceId = vehicle.Value,
            chargerDeviceId = charger.Value,
            targetSocPercent = target,
            departureAt = departure.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        }, Ct);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string goalId, string json) =>
        client.PatchAsync(new Uri($"/v1/goals/{goalId}", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.EndsWith("/" + type, problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        if (field is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        return problem;
    }

    private async Task<(DeviceId Vehicle, DeviceId Charger)> DevicesAsync(double? soc = 40.0)
    {
        var now = DateTimeOffset.UtcNow;
        await GoalSeeds.PricesAsync(shared.Database, now, now.AddHours(14), 150m);
        return await GoalSeeds.CarAndWallboxAsync(shared.Database, OwnerA, "a", soc);
    }

    private async Task<string> CreateGoalAsync()
    {
        var (vehicle, charger) = await DevicesAsync();
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        using var response = await PostAsync(client, vehicle, charger, 80, Departure());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("goal").GetProperty("id").GetString()!;
    }
}
