using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Devices;
using CheapHours.Host.Polling;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Host;

/// <summary>Recording a completed poll (design §6) with an in-memory store and publisher.</summary>
public sealed class PollResultRecorderTests : IDisposable
{
    private static readonly DateTimeOffset At1800 = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly ProviderAccount Account = new("scripted", OwnerId.Parse("own_demo"), "acct_demo");
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly InMemoryDeviceStore _devices = new();
    private readonly InMemoryPublisher _publisher = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly LoggerFactory _loggers;
    private readonly ScriptedPollingProvider _provider = new();
    private readonly PollResultRecorder _recorder;

    public PollResultRecorderTests()
    {
        _loggers = new LoggerFactory([_logs]);
        var time = new FakeTimeProvider(At1800.AddSeconds(5));
        var events = new DeviceEventRecorder(_devices, _publisher, _loggers.CreateLogger<DeviceEventRecorder>());
        _recorder = new PollResultRecorder(_devices, events, time, _loggers.CreateLogger<PollResultRecorder>());
    }

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static IReadOnlyList<DeviceEvent> Vehicle(string id, DateTimeOffset at, long energyWh = 3_250) =>
    [
        new DeviceEvent.Discovered(id, at, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 77_000)),
        new DeviceEvent.Connected(id, at, new DeviceHardware("Voltwerk", "Model E")),
        new DeviceEvent.StatusChanged(id, at, DeviceStatus.Charging),
        new DeviceEvent.ReadingReported(new DeviceReading(id, at, 11_000, energyWh, 42.5m)),
    ];

    private Task RecordAsync(IReadOnlyList<DeviceEvent> events) =>
        _recorder.RecordAsync(_provider, Account, Interval, new PollResult.Completed(events), TestContext.Current.CancellationToken);

    [Fact]
    public async Task First_discovery_creates_the_device_under_the_accounts_owner()
    {
        await RecordAsync(Vehicle("veh_1", At1800));

        var (device, interval, at) = Assert.Single(_devices.Ensured);
        Assert.Equal(OwnerId.Parse("own_demo"), device.OwnerId);
        Assert.Equal("scripted", device.ProviderId);
        Assert.Equal(DeviceType.Vehicle, device.Type);
        Assert.Equal(77_000, device.Attributes.BatteryCapacityWh);
        Assert.Equal(Capabilities.ReadTelemetry | Capabilities.ReadStateOfCharge | Capabilities.StartStop, device.Capabilities);
        Assert.Equal(Interval, interval);
        Assert.Equal(At1800, at);
        Assert.Equal(["ensure veh_1", "hardware veh_1 Model E", "status veh_1 Charging", $"seen veh_1 {At1800:O}"], _devices.Calls);
        var message = Assert.Single(_publisher.Telemetry);
        Assert.Equal(device.Id, message.DeviceId);
        Assert.Equal("scripted", message.ProviderId);
        Assert.Equal(8, message.MessageId.Version);
    }

    [Fact]
    public async Task Second_discovery_keeps_the_device_id()
    {
        await RecordAsync(Vehicle("veh_1", At1800));
        await RecordAsync(Vehicle("veh_1", At1800.AddMinutes(1), energyWh: 3_400));

        var ids = _publisher.Telemetry.Select(m => m.DeviceId).Distinct();
        Assert.Single(ids);
        Assert.Equal(2, _devices.Ensured.Count);
    }

    [Fact]
    public async Task A_reading_without_discovery_for_an_unknown_device_is_skipped_with_a_warning()
    {
        await RecordAsync([new DeviceEvent.ReadingReported(new DeviceReading("veh_x", At1800, null, 100))]);

        Assert.Empty(_publisher.Published);
        Assert.Empty(_devices.Calls);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("veh_x", StringComparison.Ordinal) && l.Contains("scripted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reading_for_a_known_device_needs_no_discovery()
    {
        var known = new Device(DeviceId.Parse("dev_known"), OwnerId.Parse("own_demo"), "scripted", "veh_k", DeviceType.Vehicle, Capabilities.ReadTelemetry, new DeviceAttributes(batteryCapacityWh: 50_000));
        _devices.Add(known);

        await RecordAsync([new DeviceEvent.ReadingReported(new DeviceReading("veh_k", At1800, null, 100))]);

        Assert.Equal(known.Id, Assert.Single(_publisher.Telemetry).DeviceId);
    }

    [Fact]
    public async Task Malformed_input_is_logged_with_its_reason()
    {
        await RecordAsync([new DeviceEvent.MalformedInput(At1800, "vehicle veh_2: state timestamp missing or without an offset"), .. Vehicle("veh_1", At1800)]);

        Assert.Contains(_logs.Lines, l => l.Contains("state timestamp missing or without an offset", StringComparison.Ordinal));
        Assert.Single(_publisher.Telemetry);
    }

    [Fact]
    public async Task A_removal_of_a_known_device_marks_it_and_one_of_an_unknown_device_is_only_logged()
    {
        await RecordAsync(Vehicle("veh_2", At1800));
        _devices.Calls.Clear();

        await RecordAsync([new DeviceEvent.DeviceRemoved("veh_2", At1800.AddMinutes(1)), new DeviceEvent.DeviceRemoved("veh_ghost", At1800.AddMinutes(1))]);

        Assert.Equal(["removed veh_2"], _devices.Calls);
        Assert.Contains(_logs.Lines, l => l.Contains("veh_ghost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_state_gives_the_same_message_id()
    {
        await RecordAsync(Vehicle("veh_1", At1800));
        await RecordAsync(Vehicle("veh_1", At1800));
        await RecordAsync(Vehicle("veh_1", At1800.AddMinutes(1)));

        var ids = _publisher.Telemetry.Select(m => m.MessageId).ToList();
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
    }

    [Fact]
    public async Task Each_vehicle_of_a_poll_is_recorded_on_its_own()
    {
        await RecordAsync([.. Vehicle("veh_1", At1800), .. Vehicle("veh_2", At1800.AddSeconds(1))]);

        Assert.Equal(2, _publisher.Telemetry.Select(m => m.DeviceId).Distinct().Count());
        Assert.All(_publisher.Telemetry, m => Assert.Single(m.Readings));
    }
}
