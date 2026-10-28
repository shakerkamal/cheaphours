using System.Net;
using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Api;

public sealed class ApiHealthTests(MongoFixture mongo)
{
    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Live_and_ready_answer_200_without_a_key_once_keys_are_seeded()
    {
        await using var api = new ApiFactory(mongo.ConnectionString);
        await api.StartAsync();
        using var client = api.CreateClient(apiKey: null);

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/live"));
        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/ready"));
    }

    [Fact]
    public async Task Ready_answers_503_without_details_when_mongodb_is_unreachable()
    {
        await using var api = new ApiFactory("mongodb://127.0.0.1:1/?directConnection=true", overrides: new Dictionary<string, string?>
        {
            ["Mongo:ServerSelectionTimeout"] = "00:00:00.500",
        });
        await api.StartAsync(waitUntilReady: false);
        using var client = api.CreateClient(apiKey: null);

        var live = await GetAsync(client, "/health/live");
        var ready = await GetAsync(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), ready);
    }
}
