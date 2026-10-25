using System.Net.WebSockets;
using System.Text;

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
