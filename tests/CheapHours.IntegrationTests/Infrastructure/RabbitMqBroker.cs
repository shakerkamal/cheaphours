using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using Testcontainers.RabbitMq;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// A RabbitMQ container bound to a fixed host port, so stopping and starting it keeps its address and a
/// client can reconnect.
/// </summary>
public sealed class RabbitMqBroker : IAsyncDisposable
{
    private const int AmqpPort = 5672;

    private readonly RabbitMqContainer _container;

    private RabbitMqBroker(RabbitMqContainer container, string connectionString)
    {
        _container = container;
        ConnectionString = connectionString;
    }

    /// <summary>Gets the AMQP connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>Stops the broker; its address stays reserved.</summary>
    /// <returns>A task that completes when it has stopped.</returns>
    public Task StopAsync() => _container.StopAsync();

    /// <summary>Starts the broker again on the same address.</summary>
    /// <returns>A task that completes when it accepts connections.</returns>
    public Task StartAsync() => _container.StartAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal static async Task<RabbitMqBroker> LaunchAsync()
    {
        var user = "test";
        var password = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var hostPort = FreePort();
        var container = new RabbitMqBuilder(RabbitMqFixture.Image)
            .WithUsername(user)
            .WithPassword(password)
            .WithPortBinding(hostPort, AmqpPort)
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (DockerUnavailableException ex)
        {
            throw new InvalidOperationException(MongoFixture.DockerRequiredMessage, ex);
        }

        return new RabbitMqBroker(container, $"amqp://{user}:{password}@{container.Hostname}:{hostPort}/");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
