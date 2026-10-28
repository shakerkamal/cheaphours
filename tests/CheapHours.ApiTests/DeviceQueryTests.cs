using System.Net;

namespace CheapHours.ApiTests;

/// <summary>
/// Query validation on the device endpoints. It happens before any lookup, so these run without a database:
/// a request that reached MongoDB here would fail with 500 instead of 400.
/// </summary>
public sealed class DeviceQueryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/v1/devices?limit=0", "limit")]
    [InlineData("/v1/devices?limit=500", "limit")]
    [InlineData("/v1/devices?cursor=not-a-cursor", "cursor")]
    public async Task Invalid_device_list_query_gets_400_keyed_by_the_parameter(string path, string parameter)
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), $"No error for '{parameter}': {problem}");
    }

    [Theory]
    [InlineData("from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&resolution=1h", "resolution")]
    [InlineData("from=2026-10-01T00:00:00%2B02:00&to=2026-10-02T00:00:00Z", "from")]
    [InlineData("from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&limit=201", "limit")]
    [InlineData("from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", "to")]
    [InlineData("from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&cursor=not-a-cursor", "cursor")]
    public async Task Invalid_telemetry_query_gets_400_keyed_by_the_parameter(string query, string parameter)
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri($"/v1/devices/dev_wallbox_01/telemetry?{query}", UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), $"No error for '{parameter}': {problem}");
    }

    [Fact]
    public async Task Invalid_query_for_an_unknown_device_is_400_not_404()
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(
            new Uri("/v1/devices/dev_does_not_exist/telemetry?from=2026-09-01T00:00:00Z&to=2026-10-11T00:00:00Z", UriKind.Relative),
            TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }
}
