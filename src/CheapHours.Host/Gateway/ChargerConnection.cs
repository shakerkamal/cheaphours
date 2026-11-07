using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CheapHours.Core.Ids;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Host.Gateway;

/// <summary>
/// One open charger WebSocket on this replica. Sends are serialised (one at a time), the close reason is
/// set once (the first cause wins), and <see cref="Stopping"/> ends the receive loop.
/// </summary>
internal sealed class ChargerConnection : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(OcppFrame Frame, DateTimeOffset At)?>> _pending = new(StringComparer.Ordinal);
    private int _callsClosed;
    private long _lastSeenTicks;
    private int _reason;
    private int _disposed;

    /// <summary>Creates the connection.</summary>
    /// <param name="chargePointId">The charge point id.</param>
    /// <param name="socket">The accepted WebSocket.</param>
    /// <param name="connectedAt">When it was accepted (UTC); also the first "last seen" time.</param>
    public ChargerConnection(string chargePointId, WebSocket socket, DateTimeOffset connectedAt)
    {
        ChargePointId = chargePointId;
        Socket = socket;
        _lastSeenTicks = connectedAt.UtcTicks;
    }

    /// <summary>Gets the charge point id.</summary>
    public string ChargePointId { get; }

    /// <summary>Gets the id of this connection, stored in the registry.</summary>
    public Guid ConnectionId { get; } = Guid.NewGuid();

    /// <summary>Gets or sets the charger's device id; set once, when the connection is accepted.</summary>
    public DeviceId DeviceId { get; set; }

    /// <summary>Gets the socket.</summary>
    public WebSocket Socket { get; }

    /// <summary>Gets when anything was last received (UTC).</summary>
    public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

    /// <summary>Gets why the connection ended, or <see cref="CloseReason.None"/> while it is open.</summary>
    public CloseReason Reason => (CloseReason)Volatile.Read(ref _reason);

    /// <summary>Gets a token that is cancelled when the receive loop must stop.</summary>
    public CancellationToken Stopping => _stop.Token;

    /// <summary>Gets a task that completes once the connection's exit path has run.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Records that something was received at <paramref name="at"/>; never moves backwards.</summary>
    /// <param name="at">The receipt time (UTC).</param>
    public void Seen(DateTimeOffset at)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _lastSeenTicks);
            if (at.UtcTicks <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _lastSeenTicks, at.UtcTicks, current) != current);
    }

    /// <summary>Sets the close reason unless one is already set.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns><see langword="true"/> when this call set it.</returns>
    public bool TrySetReason(CloseReason reason) =>
        Interlocked.CompareExchange(ref _reason, (int)reason, (int)CloseReason.None) == (int)CloseReason.None;

    /// <summary>Sends one text frame; waits for any send in progress.</summary>
    /// <param name="text">The frame text.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when sent.</returns>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>
    /// Sends <c>[2, uniqueId, action, payload]</c> and waits for the charger's answer with that unique id. The
    /// receive loop completes the wait (<see cref="TryCompleteCall"/>); a closed connection fails it.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="payload">The request payload.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="time">The clock for the timeout.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer, or why there is none.</returns>
    public async Task<ChargerCallOutcome> CallAsync(string action, JsonObject payload, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        var uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource<(OcppFrame Frame, DateTimeOffset At)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[uniqueId] = reply;
        try
        {
            // A connection that already ended takes no new calls.
            if (Volatile.Read(ref _callsClosed) != 0)
            {
                return new ChargerCallOutcome.Closed();
            }

            try
            {
                await SendAsync(OcppFrameCodec.WriteCall(uniqueId, action, payload), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                return new ChargerCallOutcome.Closed();
            }

            try
            {
                return await reply.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false) is { } answer
                    ? new ChargerCallOutcome.Replied(uniqueId, answer.Frame, answer.At)
                    : new ChargerCallOutcome.Closed();
            }
            catch (TimeoutException)
            {
                return new ChargerCallOutcome.TimedOut();
            }
        }
        finally
        {
            _pending.TryRemove(uniqueId, out _);
        }
    }

    /// <summary>Completes the waiting call that <paramref name="frame"/> answers, if there is one.</summary>
    /// <param name="frame">A <see cref="OcppCallResult"/> or <see cref="OcppCallError"/> from the charger.</param>
    /// <param name="receivedAt">When the frame was received (UTC).</param>
    /// <returns><see langword="true"/> when the frame answered a call this connection sent.</returns>
    public bool TryCompleteCall(OcppFrame frame, DateTimeOffset receivedAt)
    {
        var uniqueId = frame switch
        {
            OcppCallResult result => result.UniqueId,
            OcppCallError error => error.UniqueId,
            _ => null,
        };
        return uniqueId is not null && _pending.TryRemove(uniqueId, out var reply) && reply.TrySetResult((frame, receivedAt));
    }

    /// <summary>Fails every waiting call as closed; later calls fail at once.</summary>
    public void FailPendingCalls()
    {
        Volatile.Write(ref _callsClosed, 1);
        foreach (var uniqueId in _pending.Keys)
        {
            if (_pending.TryRemove(uniqueId, out var reply))
            {
                reply.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// Starts the close handshake with <paramref name="status"/>. If it cannot be sent within
    /// <paramref name="timeout"/> the socket is aborted; if the charger does not answer, the receive loop is
    /// stopped after <paramref name="timeout"/>.
    /// </summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="status">The close status sent to the charger.</param>
    /// <param name="description">A short, fixed description.</param>
    /// <param name="timeout">How long to wait for the handshake.</param>
    /// <returns>A task that completes when the close frame was sent or the socket was aborted.</returns>
    public async Task CloseAsync(CloseReason reason, WebSocketCloseStatus status, string description, TimeSpan timeout)
    {
        TrySetReason(reason);
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await _send.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(status, description, timeoutSource.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _send.Release();
            }

            Stop(after: timeout);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            Abort(reason);
        }
    }

    /// <summary>Aborts the socket at once and stops the receive loop (for dead connections).</summary>
    /// <param name="reason">Why.</param>
    public void Abort(CloseReason reason)
    {
        TrySetReason(reason);
        Socket.Abort();
        Stop();
    }

    /// <summary>Marks the exit path as done.</summary>
    public void MarkEnded() => _ended.TrySetResult();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            FailPendingCalls();
            _stop.Dispose();
            _send.Dispose();
        }
    }

    // Stops the receive loop now, or after a delay; a no-op once disposed.
    private void Stop(TimeSpan? after = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (after is { } delay)
            {
                _stop.CancelAfter(delay);
            }
            else
            {
                _stop.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently: the loop has already ended.
        }
    }
}
