using System.Net;

namespace CheapHours.ApiTests;

/// <summary>
/// <c>GET /v1/goals</c> without a database: the key and the query are checked before any lookup, so a request
/// that reached MongoDB here would fail with 500 instead of 400 (planning-api "Unreadable cursor").
/// </summary>
public sealed class GoalListQueryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Missing_key_gets_401()
    {
        using var client = factory.CreateClient(apiKey: null);

        using var response = await client.GetAsync(new Uri("/v1/goals", UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData("/v1/goals?limit=500", "limit")]
    [InlineData("/v1/goals?limit=0", "limit")]
    [InlineData("/v1/goals?cursor=not-a-cursor", "cursor")]
    public async Task Invalid_goal_list_query_gets_400_keyed_by_the_parameter(string path, string parameter)
    {
        using var client = factory.CreateClient(ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), $"No error for '{parameter}': {problem}");
    }
}
