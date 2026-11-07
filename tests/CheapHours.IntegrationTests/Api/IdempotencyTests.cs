using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CheapHours.Core;
using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Idempotency;
using CheapHours.IntegrationTests.Goals;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Persistence;
using CheapHours.Persistence.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// <c>Idempotency-Key</c> on <c>POST /v1/goals</c> (public-api spec, design §10). Replays, reuse, owners and key
/// checks go through the real API; races, abandonment, 5xx and expiry drive the middleware directly with the
/// real store and a fake clock, so their timing is exact.
/// </summary>
public sealed class IdempotencyTests(SharedApi shared, MongoFixture mongo) : IClassFixture<SharedApi>
{
    private static readonly OwnerId OwnerA = OwnerId.Parse("own_a");
    private static readonly OwnerId OwnerB = OwnerId.Parse("own_b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_key_and_body_replays_the_first_response_and_stores_one_goal()
    {
        var (vehicle, charger) = await DevicesAsync(OwnerA);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        var key = NewKey();

        using var first = await PostAsync(client, key, Body(vehicle, charger, 80));
        using var second = await PostAsync(client, key, Body(vehicle, charger, 80));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        var goals = shared.Database.GetCollection<BsonDocument>(MongoIndexes.Goals);
        Assert.Equal(1, await goals.CountDocumentsAsync(new BsonDocument("vehicleDeviceId", vehicle.Value), cancellationToken: Ct));
    }

    [Fact]
    public async Task Same_key_with_another_body_gets_409_key_reused()
    {
        var (vehicle, charger) = await DevicesAsync(OwnerA);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        var key = NewKey();
        using var first = await PostAsync(client, key, Body(vehicle, charger, 80));

        using var second = await PostAsync(client, key, Body(vehicle, charger, 90));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.EndsWith("/idempotency-key-reused", (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unprocessable_is_replayed_without_planning_again()
    {
        var (vehicle, charger) = await DevicesAsync(OwnerA, soc: null);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        var key = NewKey();
        var body = Body(vehicle, charger, 80);
        using var first = await PostAsync(client, key, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);

        // Now the goal could be planned, but the repeat gets the first answer.
        await GoalSeeds.SetChargeAsync(shared.Database, vehicle, 40.0, DateTimeOffset.UtcNow);
        using var second = await PostAsync(client, key, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, await shared.Database.GetCollection<BsonDocument>(MongoIndexes.Goals).CountDocumentsAsync(new BsonDocument("vehicleDeviceId", vehicle.Value), cancellationToken: Ct));
    }

    [Fact]
    public async Task Two_owners_may_use_the_same_key()
    {
        var (vehicleA, chargerA) = await DevicesAsync(OwnerA);
        var (vehicleB, chargerB) = await DevicesAsync(OwnerB);
        using var clientA = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        using var clientB = shared.Api.CreateClient(ApiFactory.ReadKeyB);

        using var a = await PostAsync(clientA, "abc", Body(vehicleA, chargerA, 80));
        using var b = await PostAsync(clientB, "abc", Body(vehicleB, chargerB, 80));

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.NotEqual(a.Headers.Location, b.Headers.Location);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("has space")]
    public async Task Key_outside_1_to_255_visible_characters_gets_400(string kind)
    {
        var (vehicle, charger) = await DevicesAsync(OwnerA);
        using var client = shared.Api.CreateClient(ApiFactory.ReadKeyA);
        var key = kind == "long" ? new string('k', 300) : "two words";

        using var response = await PostAsync(client, key, Body(vehicle, charger, 80));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _), problem.ToString());
    }

    [Fact]
    public async Task A_repeat_while_the_first_runs_gets_409_conflict()
    {
        var harness = await HarnessAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var middleware = harness.Middleware(async ctx =>
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await gate.Task;
            ctx.Response.StatusCode = StatusCodes.Status201Created;
        });
        var key = NewKey();

        var first = MiddlewareHarness.SendAsync(middleware, key, "{}");
        await entered.Task.WaitAsync(Ct);
        var second = await MiddlewareHarness.SendAsync(middleware, key, "{}");
        gate.SetResult();

        Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);
        Assert.Contains("/conflict", second.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status201Created, (await first).StatusCode);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task An_abandoned_first_request_is_taken_over_after_a_minute()
    {
        var harness = await HarnessAsync();
        var key = NewKey();
        var middleware = harness.Middleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        });

        // A first request that started and never finished (its replica died).
        await harness.Store.StartAsync($"{OwnerA}|{key}", "fingerprint-of-the-lost-request", harness.Clock.GetUtcNow(), IdempotencyMiddleware.KeepFor, IdempotencyMiddleware.AbandonAfter, Ct);

        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(StatusCodes.Status409Conflict, (await MiddlewareHarness.SendAsync(middleware, key, "{}")).StatusCode);

        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(StatusCodes.Status201Created, (await MiddlewareHarness.SendAsync(middleware, key, "{}")).StatusCode);
    }

    [Fact]
    public async Task A_server_error_is_not_remembered()
    {
        var harness = await HarnessAsync();
        var key = NewKey();
        var calls = 0;
        var middleware = harness.Middleware(ctx =>
        {
            ctx.Response.StatusCode = Interlocked.Increment(ref calls) == 1 ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status201Created;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await MiddlewareHarness.SendAsync(middleware, key, "{}")).StatusCode);
        Assert.Equal(StatusCodes.Status201Created, (await MiddlewareHarness.SendAsync(middleware, key, "{}")).StatusCode);
        Assert.Equal(StatusCodes.Status201Created, (await MiddlewareHarness.SendAsync(middleware, key, "{}")).StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_key_reused_after_25_hours_is_handled_as_new()
    {
        var harness = await HarnessAsync();
        var key = NewKey();
        var calls = 0;
        var middleware = harness.Middleware(ctx =>
        {
            Interlocked.Increment(ref calls);
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        });
        await MiddlewareHarness.SendAsync(middleware, key, """{"targetSocPercent":80}""");

        harness.Clock.Advance(TimeSpan.FromHours(25));
        var later = await MiddlewareHarness.SendAsync(middleware, key, """{"targetSocPercent":90}""");

        Assert.Equal(StatusCodes.Status201Created, later.StatusCode);
        Assert.Equal(2, calls);
    }

    private static string NewKey() => "key-" + Guid.NewGuid().ToString("N");

    private static string Body(DeviceId vehicle, DeviceId charger, int target) => JsonSerializer.Serialize(new
    {
        vehicleDeviceId = vehicle.Value,
        chargerDeviceId = charger.Value,
        targetSocPercent = target,
        departureAt = SlotGrid.Floor(DateTimeOffset.UtcNow).AddHours(13).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/goals", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(request, Ct);
    }

    private async Task<(DeviceId Vehicle, DeviceId Charger)> DevicesAsync(OwnerId owner, double? soc = 40.0)
    {
        var now = DateTimeOffset.UtcNow;
        await GoalSeeds.PricesAsync(shared.Database, now, now.AddHours(14), 150m);
        return await GoalSeeds.CarAndWallboxAsync(shared.Database, owner, "idem", soc);
    }

    private async Task<MiddlewareHarness> HarnessAsync() =>
        new(new IdempotencyStore(await SharedDatabases.GetAsync(mongo, nameof(IdempotencyTests))), new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero)));

    /// <summary>Runs the middleware on a hand-built request to an idempotent endpoint, as owner A.</summary>
    private sealed class MiddlewareHarness(IdempotencyStore store, FakeTimeProvider clock)
    {
        private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().BuildServiceProvider();

        public IdempotencyStore Store => store;

        public FakeTimeProvider Clock => clock;

        public IdempotencyMiddleware Middleware(RequestDelegate next) => new(next, store, clock);

        public static async Task<(int StatusCode, string Body)> SendAsync(IdempotencyMiddleware middleware, string key, string body)
        {
            var context = new DefaultHttpContext { RequestServices = Services };
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(IdempotentMetadata.Instance), "POST /v1/goals"));
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ApiScopes.OwnerClaimType, OwnerA.Value)], "test"));
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/v1/goals";
            context.Request.Headers["Idempotency-Key"] = key;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var response = new MemoryStream();
            context.Response.Body = response;

            await middleware.InvokeAsync(context);

            return (context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
        }
    }
}
