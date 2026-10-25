using System.Net;
using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewayHealthTests(MongoFixture mongo)
{
    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Live_and_ready_answer_200_when_mongodb_is_up()
    {
        await using var gateway = new GatewayFactory(mongo.ConnectionString);
        await gateway.StartAsync();
        using var client = gateway.CreateClient();

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/live"));
        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/ready"));
    }

    [Fact]
    public async Task Ready_answers_503_without_details_when_mongodb_is_unreachable()
    {
        await using var gateway = new GatewayFactory("mongodb://127.0.0.1:1/?directConnection=true", overrides: new Dictionary<string, string?>
        {
            ["Mongo:ServerSelectionTimeout"] = "00:00:00.500",
        });
        await gateway.StartAsync(waitUntilReady: false);
        using var client = gateway.CreateClient();

        var live = await GetAsync(client, "/health/live");
        var ready = await GetAsync(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("Unhealthy", ready.Body);
    }

    [Fact]
    public async Task Chargers_get_503_until_the_gateway_is_initialised()
    {
        await using var gateway = new GatewayFactory("mongodb://127.0.0.1:1/?directConnection=true", overrides: new Dictionary<string, string?>
        {
            ["Mongo:ServerSelectionTimeout"] = "00:00:00.500",
        });
        await gateway.StartAsync(waitUntilReady: false);
        using var client = gateway.CreateClient();

        var (status, _, _) = await OcppTestClient.UpgradeAsync(client, "/ocpp/CP001", OcppTestClient.Basic("CP001", GatewayFactory.Password));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }
}
