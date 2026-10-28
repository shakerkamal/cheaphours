using System.Net;
using System.Text;

namespace CheapHours.ApiTests;

public sealed class EndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Range = "from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z";

    public static TheoryData<string, string, string?> V1Endpoints => new()
    {
        { "GET", "/v1/devices", null },
        { "GET", "/v1/devices/dev_wallbox_01", null },
        { "GET", $"/v1/devices/dev_wallbox_01/telemetry?{Range}&resolution=15m", null },
        { "GET", $"/v1/prices?zone=DE-LU&{Range}", null },
        { "POST", "/v1/goals", """{"vehicleDeviceId":"dev_car_01","chargerDeviceId":"dev_wallbox_01","targetSocPercent":80,"departureAt":"2026-10-17T05:00:00Z"}""" },
        { "GET", "/v1/goals/goal_1/plan", null },
        { "POST", "/v1/plans/plan_1/confirm", null },
        { "POST", "/v1/devices/dev_wallbox_01/commands", """{"type":"SetPowerLimit","parameters":{"maxPowerW":3700}}""" },
        { "GET", "/v1/commands/cmd_01JB7Z8K2Q", null },
        { "GET", $"/v1/savings?{Range}", null },
    };

    // Built endpoints read MongoDB, which these tests do not have; the integration tests cover them.
    public static TheoryData<string, string, string?> NotBuiltEndpoints
    {
        get
        {
            string[] built = ["GET /v1/devices", "GET /v1/devices/dev_wallbox_01", $"GET /v1/devices/dev_wallbox_01/telemetry?{Range}&resolution=15m"];
            var data = new TheoryData<string, string, string?>();
            foreach (var row in V1Endpoints)
            {
                var (method, path, body) = row.Data;
                if (!built.Contains($"{method} {path}", StringComparer.Ordinal))
                {
                    data.Add(method, path, body);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NotBuiltEndpoints))]
    public async Task Every_unbuilt_v1_endpoint_answers_501_after_authentication(string method, string path, string? body)
    {
        using var client = factory.CreateClient(ApiFactory.ExecuteKey);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "5f1c2a8e-3d7b-4a10-9c55-1b2e7f9d0a44");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotImplemented, "not-implemented");
    }

    [Theory]
    [MemberData(nameof(V1Endpoints))]
    public async Task Every_v1_endpoint_requires_a_key(string method, string path, string? body)
    {
        using var client = factory.CreateClient(apiKey: null);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unversioned_path_gets_404()
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri("/devices", UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Id_with_the_wrong_prefix_gets_400()
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri("/v1/devices/plan_1", UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }
}

public sealed class GatewayRoleTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Fact]
    public async Task Gateway_role_does_not_serve_the_public_api()
    {
        using var client = factory.CreateClient(ApiFactory.ExecuteKey);

        using var response = await client.GetAsync(new Uri("/v1/devices", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
