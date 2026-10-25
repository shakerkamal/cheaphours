using System.Net.WebSockets;
using CheapHours.Host.Gateway;

namespace CheapHours.UnitTests.Host;

public sealed class ConnectionTableTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    internal static ChargerConnection Connection(string id, DateTimeOffset? at = null) =>
        new(id, WebSocket.CreateFromStream(new MemoryStream(), isServer: true, subProtocol: "ocpp1.6", keepAliveInterval: Timeout.InfiniteTimeSpan), at ?? T0);

    private readonly ConnectionTable _table = new();

    [Fact]
    public void First_connection_replaces_nothing()
    {
        using var first = Connection("CP001");

        Assert.Null(_table.Add(first));
        Assert.Same(first, _table.Find("CP001"));
        Assert.Equal(1, _table.Count);
    }

    [Fact]
    public void Second_connection_returns_the_old_one_to_close()
    {
        using var first = Connection("CP001");
        using var second = Connection("CP001");
        _table.Add(first);

        Assert.Same(first, _table.Add(second));
        Assert.Same(second, _table.Find("CP001"));
        Assert.Equal(1, _table.Count);
    }

    [Fact]
    public void Removing_a_replaced_connection_keeps_the_newer_one()
    {
        using var first = Connection("CP001");
        using var second = Connection("CP001");
        _table.Add(first);
        _table.Add(second);

        Assert.False(_table.Remove(first));
        Assert.Same(second, _table.Find("CP001"));
    }

    [Fact]
    public void Removing_the_current_connection_empties_the_slot()
    {
        using var first = Connection("CP001");
        _table.Add(first);

        Assert.True(_table.Remove(first));
        Assert.Null(_table.Find("CP001"));
        Assert.Equal(0, _table.Count);
    }

    [Fact]
    public void Snapshot_lists_every_charger()
    {
        using var a = Connection("CP001");
        using var b = Connection("CP002");
        _table.Add(a);
        _table.Add(b);

        Assert.Equal(["CP001", "CP002"], _table.Snapshot().Select(c => c.ChargePointId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Close_reason_is_set_once()
    {
        using var connection = Connection("CP001");

        Assert.True(connection.TrySetReason(CloseReason.Replaced));
        Assert.False(connection.TrySetReason(CloseReason.ClientClosed));
        Assert.Equal(CloseReason.Replaced, connection.Reason);
    }

    [Fact]
    public void Last_seen_starts_at_connect_and_never_moves_back()
    {
        using var connection = Connection("CP001");
        Assert.Equal(T0, connection.LastSeenAt);

        connection.Seen(T0.AddSeconds(30));
        connection.Seen(T0.AddSeconds(10));

        Assert.Equal(T0.AddSeconds(30), connection.LastSeenAt);
    }

    [Fact]
    public void Abort_stops_the_receive_loop()
    {
        using var connection = Connection("CP001");

        connection.Abort(CloseReason.HeartbeatTimeout);

        Assert.True(connection.Stopping.IsCancellationRequested);
        Assert.Equal(CloseReason.HeartbeatTimeout, connection.Reason);
        Assert.Equal(WebSocketState.Aborted, connection.Socket.State);
    }
}
