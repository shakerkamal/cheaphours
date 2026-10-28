using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace CheapHours.Messaging;

/// <summary>
/// The process's one connection to RabbitMQ. It recovers by itself after the broker restarts (channels,
/// consumers and declared topology come back). Only the broker's <c>host:port</c> and exception type names
/// are ever logged: the connection string holds the password, and client exceptions can echo it.
/// </summary>
public sealed partial class RabbitMqConnection : IAsyncDisposable
{
    private const int DefaultAmqpPort = 5672;

    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnection? _connection;

    /// <summary>Creates the connection; nothing is opened until <see cref="ConnectAsync"/>.</summary>
    /// <param name="connectionString">The AMQP URI, with credentials.</param>
    /// <param name="clientName">The name the broker shows for this connection, for example <c>cheaphours-worker</c>.</param>
    /// <param name="logger">The logger.</param>
    public RabbitMqConnection(string connectionString, string clientName, ILogger<RabbitMqConnection> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(logger);

        var uri = new Uri(connectionString);
        Endpoint = $"{uri.Host}:{(uri.Port > 0 ? uri.Port : DefaultAmqpPort)}";
        _factory = new ConnectionFactory
        {
            Uri = uri,
            ClientProvidedName = clientName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
            RequestedHeartbeat = TimeSpan.FromSeconds(15),
        };
        _logger = logger;
    }

    /// <summary>Gets the broker's <c>host:port</c>, safe to log.</summary>
    public string Endpoint { get; }

    /// <summary>Gets whether the connection is open now (false before connecting and while recovering).</summary>
    public bool IsOpen => _connection?.IsOpen == true;

    /// <summary>Gets the open connection.</summary>
    /// <exception cref="BrokerUnavailableException">There is no open connection right now.</exception>
    public IConnection Current => _connection is { IsOpen: true } open ? open : throw new BrokerUnavailableException(Endpoint);

    /// <summary>Opens the connection once; later calls return the same one.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="BrokerUnavailableException">The broker could not be reached or refused the credentials.</exception>
    public async Task<IConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            try
            {
                _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConnectFailed(Endpoint, ErrorNames(ex));
                throw new BrokerUnavailableException(Endpoint);
            }

            LogConnected(Endpoint);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();
    }

    // The type names of the exception and its inner exceptions: enough to tell "unreachable" from "bad
    // credentials" without ever printing a message that might hold the URI.
    private static string ErrorNames(Exception ex)
    {
        var names = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            names.Add(e.GetType().Name);
        }

        return string.Join("/", names);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Endpoint}")]
    private partial void LogConnected(string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ at {Endpoint} is not reachable ({Error})")]
    private partial void LogConnectFailed(string endpoint, string error);
}
