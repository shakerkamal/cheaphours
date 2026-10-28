using System.Net.WebSockets;
using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging;
using CheapHours.Persistence.ChargePoints;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Runs one accepted charger connection: registers it, reads frames one at a time, stores their effects,
/// answers, and on exit decides (by <see cref="CloseReason"/>) whether the device goes offline and the
/// registry entry goes.
/// </summary>
internal sealed partial class ChargerSession(
    ConnectionTable connections,
    IDeviceStateStore devices,
    IConnectionRegistry registry,
    DeviceEventRecorder recorder,
    OcppProvider provider,
    TimeProvider time,
    IOptions<GatewayOptions> options,
    IOptions<OcppOptions> ocppOptions,
    ILogger<ChargerSession> logger)
{
    /// <summary>The largest message accepted; larger ones close the connection with 1009.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>How long a close handshake may take before the socket is aborted.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What a charger device can do (OCPP 1.6 wallbox).</summary>
    public const Capabilities ChargerCapabilities = Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit;

    private readonly string _replicaId = options.Value.ReplicaId;
    private readonly TimeSpan _heartbeatInterval = ocppOptions.Value.HeartbeatInterval;

    /// <summary>Runs the connection until it ends.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="registration">The authenticated charger's registration.</param>
    /// <param name="requestAborted">Cancelled when the HTTP request is aborted.</param>
    /// <returns>A task that completes when the connection has ended and its exit path has run.</returns>
    public async Task RunAsync(WebSocket socket, ChargePointRegistration registration, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(registration);

        var now = time.GetUtcNow();
        using var connection = new ChargerConnection(registration.ChargePointId, socket, now);

        if (!await RegisterAsync(connection, registration, now, requestAborted).ConfigureAwait(false))
        {
            connection.MarkEnded();
            return;
        }

        if (connections.Add(connection) is { } previous)
        {
            LogReplaced(connection.ChargePointId);
            _ = previous.CloseAsync(CloseReason.Replaced, WebSocketCloseStatus.PolicyViolation, "Replaced by a newer connection.", CloseTimeout);
        }

        LogConnected(connection.ChargePointId, _replicaId);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, connection.Stopping);
            await ReceiveAsync(connection, stop.Token).ConfigureAwait(false);
        }
        finally
        {
            connections.Remove(connection);
            await EndAsync(connection).ConfigureAwait(false);
            connection.MarkEnded();
        }
    }

    private async Task<bool> RegisterAsync(ChargerConnection connection, ChargePointRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var device = new Device(
                DeviceId.New(time),
                registration.OwnerId,
                OcppProvider.Id,
                registration.ChargePointId,
                DeviceType.Charger,
                ChargerCapabilities,
                new DeviceAttributes(maxPowerW: registration.MaxPowerW));

            connection.DeviceId = await devices.EnsureAsync(device, _heartbeatInterval, now, cancellationToken).ConfigureAwait(false);
            await registry.RecordAsync(new ConnectionEntry(connection.ChargePointId, _replicaId, connection.ConnectionId, now), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            LogRegisterFailed(connection.ChargePointId, ex.GetType().Name);
            await connection.CloseAsync(CloseReason.ServerError, WebSocketCloseStatus.InternalServerError, "The central system is unavailable.", CloseTimeout).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReceiveAsync(ChargerConnection connection, CancellationToken cancellationToken)
    {
        var socket = connection.Socket;
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                connection.TrySetReason(CloseReason.ClientClosed);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                connection.TrySetReason(CloseReason.ClientClosed);
                await AnswerCloseAsync(connection).ConfigureAwait(false);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await connection.CloseAsync(CloseReason.ProtocolViolation, WebSocketCloseStatus.InvalidMessageType, "Only text frames are supported.", CloseTimeout).ConfigureAwait(false);
                return;
            }

            if (message.Length + result.Count > MaxMessageBytes)
            {
                await connection.CloseAsync(CloseReason.ProtocolViolation, WebSocketCloseStatus.MessageTooBig, "Messages are limited to 64 KiB.", CloseTimeout).ConfigureAwait(false);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var bytes = message.ToArray();
            message.SetLength(0);

            try
            {
                await HandleAsync(connection, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                connection.TrySetReason(CloseReason.ClientClosed);
                return;
            }
        }
    }

    private async Task HandleAsync(ChargerConnection connection, byte[] bytes, CancellationToken cancellationToken)
    {
        var receivedAt = time.GetUtcNow();

        // Any frame, valid or not, is a sign of life.
        connection.Seen(receivedAt);
        try
        {
            await devices.RecordSeenAsync(OcppProvider.Id, connection.ChargePointId, receivedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // The in-memory time still drives the offline check; the stored one catches up with the next frame.
            var errorName = ex.GetType().Name;
            LogSeenFailed(connection.ChargePointId, errorName);
        }

        var frame = OcppProvider.Decode(bytes);
        var outcome = provider.Handle(frame, connection.ChargePointId, receivedAt);
        var reply = outcome.Reply;

        // A refused call carries nothing to record (its malformed-input event is the refusal already logged below).
        if (outcome.Events.Count > 0 && outcome.Error is null && frame is OcppCall call)
        {
            try
            {
                await recorder.RecordAsync(OcppProvider.Id, connection.ChargePointId, connection.DeviceId, outcome.Events, receivedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException or BrokerUnavailableException)
            {
                LogEffectFailed(call.Action, connection.ChargePointId, ex.GetType().Name);
                reply = OcppCallHandler.InternalErrorReply(call.UniqueId);
            }
        }

        if (outcome.Error is { } error)
        {
            var action = ActionName(frame);
            LogRefused(action, connection.ChargePointId, error);
        }

        if (reply is not null)
        {
            await connection.SendAsync(reply, cancellationToken).ConfigureAwait(false);
        }
    }

    // Action names come from the charger: only log names OCPP defines, never arbitrary text.
    private static string ActionName(OcppFrame frame) =>
        frame is OcppCall call && OcppActions.IsKnown(call.Action) ? call.Action : "(other)";

    private static async Task AnswerCloseAsync(ChargerConnection connection)
    {
        if (connection.Socket.State == WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                connection.Socket.Abort();
            }
        }
    }

    private async Task EndAsync(ChargerConnection connection)
    {
        var reason = connection.Reason == CloseReason.None ? CloseReason.ClientClosed : connection.Reason;
        var at = time.GetUtcNow();
        LogEnded(connection.ChargePointId, reason);

        try
        {
            switch (reason)
            {
                case CloseReason.Replaced:
                    // The newer connection already owns the registry entry and the device stays online.
                    break;

                case CloseReason.Shutdown:
                    // The charger will reconnect to another replica; don't flap it to offline.
                    await registry.RemoveIfCurrentAsync(connection.ChargePointId, connection.ConnectionId, CancellationToken.None).ConfigureAwait(false);
                    break;

                default:
                    await devices.MarkOfflineAsync(OcppProvider.Id, connection.ChargePointId, at, CancellationToken.None).ConfigureAwait(false);
                    await registry.RemoveIfCurrentAsync(connection.ChargePointId, connection.ConnectionId, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            LogEndFailed(connection.ChargePointId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Charger {ChargePointId} connected to replica {ReplicaId}")]
    private partial void LogConnected(string chargePointId, string replicaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Charger {ChargePointId} opened a new connection; closing the old one")]
    private partial void LogReplaced(string chargePointId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Charger {ChargePointId} disconnected ({Reason})")]
    private partial void LogEnded(string chargePointId, CloseReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answered {Action} from {ChargePointId} with {ErrorCode}")]
    private partial void LogRefused(string action, string chargePointId, OcppErrorCode errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not register charger {ChargePointId} ({Error}); closing the connection")]
    private partial void LogRegisterFailed(string chargePointId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store {Action} from {ChargePointId} ({Error}); answered InternalError")]
    private partial void LogEffectFailed(string action, string chargePointId, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not store last-seen time for {ChargePointId} ({Error})")]
    private partial void LogSeenFailed(string chargePointId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the end of {ChargePointId}'s connection ({Error})")]
    private partial void LogEndFailed(string chargePointId, string error);
}
