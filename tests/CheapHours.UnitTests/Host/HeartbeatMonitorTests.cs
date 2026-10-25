using CheapHours.Host.Gateway;
using CheapHours.Providers.Ocpp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Host;

public sealed class HeartbeatMonitorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly ConnectionTable _table = new();
    private readonly HeartbeatMonitor _monitor;

    public HeartbeatMonitorTests()
    {
        _monitor = new HeartbeatMonitor(_table, _time, Options.Create(new OcppOptions { HeartbeatIntervalSeconds = 60 }), NullLogger<HeartbeatMonitor>.Instance);
    }

    public void Dispose() => _monitor.Dispose();

    [Fact]
    public void Checks_every_quarter_interval()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), _monitor.CheckPeriod);
    }

    [Fact]
    public void Three_intervals_of_silence_is_still_online()
    {
        using var connection = ConnectionTableTests.Connection("CP001", T0);
        _table.Add(connection);

        Assert.Equal(0, _monitor.Sweep(T0.AddSeconds(180)));
        Assert.Equal(CloseReason.None, connection.Reason);
    }

    [Fact]
    public void Silence_past_three_intervals_closes_the_connection()
    {
        using var connection = ConnectionTableTests.Connection("CP001", T0);
        _table.Add(connection);

        Assert.Equal(1, _monitor.Sweep(T0.AddSeconds(196)));
        Assert.Equal(CloseReason.HeartbeatTimeout, connection.Reason);
        Assert.True(connection.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void A_frame_resets_the_timer()
    {
        using var connection = ConnectionTableTests.Connection("CP001", T0);
        _table.Add(connection);

        connection.Seen(T0.AddSeconds(170));

        Assert.Equal(0, _monitor.Sweep(T0.AddSeconds(340)));
        Assert.Equal(CloseReason.None, connection.Reason);
    }

    [Fact]
    public void Only_silent_chargers_are_closed()
    {
        using var silent = ConnectionTableTests.Connection("CP001", T0);
        using var chatty = ConnectionTableTests.Connection("CP002", T0);
        _table.Add(silent);
        _table.Add(chatty);
        chatty.Seen(T0.AddSeconds(100));

        Assert.Equal(1, _monitor.Sweep(T0.AddSeconds(200)));
        Assert.Equal(CloseReason.HeartbeatTimeout, silent.Reason);
        Assert.Equal(CloseReason.None, chatty.Reason);
    }

    [Fact]
    public async Task Running_monitor_closes_a_silent_charger_on_the_fake_clock()
    {
        using var connection = ConnectionTableTests.Connection("CP001", T0);
        _table.Add(connection);
        await _monitor.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            // ExecuteAsync starts on a background thread (.NET 10), so its timer may not exist yet when the
            // test begins. Advance one check period at a time and let the loop run between steps. The exact
            // boundaries (180 s online, 196 s offline) are covered by the Sweep tests above.
            var elapsed = TimeSpan.Zero;
            while (connection.Reason == CloseReason.None && elapsed < TimeSpan.FromMinutes(10))
            {
                _time.Advance(_monitor.CheckPeriod);
                elapsed += _monitor.CheckPeriod;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal(CloseReason.HeartbeatTimeout, connection.Reason);
            Assert.True(_time.GetUtcNow() - connection.LastSeenAt > TimeSpan.FromSeconds(180));
        }
        finally
        {
            await _monitor.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
