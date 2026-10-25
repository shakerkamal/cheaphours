using DotNet.Testcontainers.Builders;
using MongoDB.Driver;
using Testcontainers.MongoDb;

[assembly: AssemblyFixture(typeof(CheapHours.IntegrationTests.Infrastructure.MongoFixture))]

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// One throwaway MongoDB replica set for the whole test run. Each test class asks for its own database,
/// so classes never see each other's documents.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    /// <summary>The image the tests run against; the same version as docker-compose.yml.</summary>
    public const string Image = "mongo:8.0.32";

    internal const string DockerRequiredMessage = "A running Docker engine is required for the integration tests.";

    private MongoDbContainer? _container;

    /// <summary>Gets the connection string of the running replica set.</summary>
    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The MongoDB container has not started.");

    /// <summary>Gets a client for the running replica set.</summary>
    public IMongoClient Client { get; private set; } = null!;

    /// <summary>Returns a database name no other test class uses.</summary>
    /// <param name="owner">A readable prefix, usually the test class name.</param>
    /// <returns>The database name.</returns>
    public static string NewDatabaseName(string owner) =>
        $"{owner}_{Guid.NewGuid():N}"[..Math.Min(63, owner.Length + 33)];

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MongoDbBuilder(Image).WithReplicaSet().Build();
            await _container.StartAsync();
        }
        catch (DockerUnavailableException ex)
        {
            throw new InvalidOperationException(DockerRequiredMessage, ex);
        }

        Client = new MongoClient(ConnectionString);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
