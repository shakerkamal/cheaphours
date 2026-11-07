using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Api;

/// <summary>
/// One API host and database for a test class. The planning endpoint tests keep apart by their own devices,
/// goals and date ranges, so they need no host of their own.
/// </summary>
public sealed class SharedApi(MongoFixture mongo) : IAsyncLifetime
{
    public ApiFactory Api { get; private set; } = null!;

    public IMongoDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Api = new ApiFactory(mongo.ConnectionString);
        await Api.StartAsync();
        Database = mongo.Client.GetDatabase(Api.DatabaseName);
    }

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();
}
