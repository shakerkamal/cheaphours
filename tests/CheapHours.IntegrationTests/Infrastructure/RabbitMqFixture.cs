[assembly: AssemblyFixture(typeof(CheapHours.IntegrationTests.Infrastructure.RabbitMqFixture))]

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// One throwaway RabbitMQ broker for the whole test run. Test classes keep apart by giving their queues a
/// prefix of their own. Tests that stop the broker use <see cref="StartDedicatedAsync"/> instead, so they
/// never disturb other classes.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    /// <summary>The image the tests run against; the same version as docker-compose.yml.</summary>
    public const string Image = "rabbitmq:4.2.9-management";

    private RabbitMqBroker? _broker;

    /// <summary>Gets the AMQP connection string of the shared broker (it holds a per-run password).</summary>
    public string ConnectionString => _broker?.ConnectionString
        ?? throw new InvalidOperationException("The RabbitMQ container has not started.");

    /// <summary>Returns a queue-name prefix no other test class uses.</summary>
    /// <param name="owner">A readable part, usually the test class name.</param>
    /// <returns>The prefix, ending in a dot.</returns>
    public static string NewPrefix(string owner) => $"t-{owner}-{Guid.NewGuid():N}"[..Math.Min(80, owner.Length + 35)] + ".";

    /// <summary>Starts a broker of its own, for a test that stops or restarts it.</summary>
    /// <returns>The running broker; dispose it at the end of the test.</returns>
    public static Task<RabbitMqBroker> StartDedicatedAsync() => RabbitMqBroker.LaunchAsync();

    /// <summary>
    /// Gets the shared broker's connection string for code that cannot take the fixture as a constructor
    /// argument (the gateway test host). Assembly fixtures start before any test runs.
    /// </summary>
    internal static string SharedConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _broker = await RabbitMqBroker.LaunchAsync();
        SharedConnectionString = _broker.ConnectionString;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }
    }
}
