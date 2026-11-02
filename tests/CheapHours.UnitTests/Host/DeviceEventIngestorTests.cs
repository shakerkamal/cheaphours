using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Devices;
using CheapHours.Host.Polling;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Host;

/// <summary>Recording a vendor cloud's events for many devices (design §3) with an in-memory store and publisher.</summary>
public sealed class DeviceEventIngestorTests : IDisposable
{
    private static readonly DateTimeOffset At1800 = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly InMemoryDeviceStore _devices = new();
    private readonly InMemoryPublisher _publisher = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly LoggerFactory _loggers;
    private readonly ScriptedPollingProvider _provider = new();
    private readonly FakeTimeProvider _time = new(At1800.AddSeconds(5));
    private readonly DeviceEventIngestor _ingestor;

    public DeviceEventIngestorTests()
    {
        _loggers = new LoggerFactory([_logs]);
        var events = new DeviceEventRecorder(_devices, _publisher, _loggers.CreateLogger<DeviceEventRecorder>());
        _ingestor = new DeviceEventIngestor(_devices, events, _time, _loggers.CreateLogger<DeviceEventIngestor>());
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

    private Task IngestAsync(IReadOnlyList<DeviceEvent> events, OwnerId? owner = null) =>
        _ingestor.IngestAsync(_provider.ProviderId, _provider.Capabilities, owner ?? Owner, Interval, events, _time.GetUtcNow(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task First_discovery_creates_the_device_under_the_accounts_owner()
    {
        await IngestAsync(Vehicle("veh_1", At1800));

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
        Assert.Equal(At1800.AddSeconds(5), message.OccurredAt);
    }

    [Fact]
    public async Task A_discovered_device_joins_the_owner_it_is_given()
    {
        await IngestAsync(Vehicle("veh_1", At1800), OwnerId.Parse("own_alice"));
        await IngestAsync(Vehicle("veh_2", At1800), OwnerId.Parse("own_bob"));

        Assert.Equal(
            [("veh_1", OwnerId.Parse("own_alice")), ("veh_2", OwnerId.Parse("own_bob"))],
            _devices.Ensured.Select(e => (e.Device.ExternalId, e.Device.OwnerId)));
    }

    [Fact]
    public async Task Second_discovery_keeps_the_device_id()
    {
        await IngestAsync(Vehicle("veh_1", At1800));
        await IngestAsync(Vehicle("veh_1", At1800.AddMinutes(1), energyWh: 3_400));

        var ids = _publisher.Telemetry.Select(m => m.DeviceId).Distinct();
        Assert.Single(ids);
        Assert.Equal(2, _devices.Ensured.Count);
    }

    [Fact]
    public async Task A_reading_without_discovery_for_an_unknown_device_is_skipped_with_a_warning()
    {
        await IngestAsync([new DeviceEvent.ReadingReported(new DeviceReading("veh_x", At1800, null, 100))]);

        Assert.Empty(_publisher.Published);
        Assert.Empty(_devices.Calls);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("veh_x", StringComparison.Ordinal) && l.Contains("scripted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reading_for_a_known_device_needs_no_discovery()
    {
        var known = new Device(DeviceId.Parse("dev_known"), OwnerId.Parse("own_demo"), "scripted", "veh_k", DeviceType.Vehicle, Capabilities.ReadTelemetry, new DeviceAttributes(batteryCapacityWh: 50_000));
        _devices.Add(known);

        await IngestAsync([new DeviceEvent.ReadingReported(new DeviceReading("veh_k", At1800, null, 100))]);

        Assert.Equal(known.Id, Assert.Single(_publisher.Telemetry).DeviceId);
    }

    [Fact]
    public async Task Malformed_input_is_logged_with_its_reason()
    {
        await IngestAsync([new DeviceEvent.MalformedInput(At1800, "vehicle veh_2: state timestamp missing or without an offset"), .. Vehicle("veh_1", At1800)]);

        Assert.Contains(_logs.Lines, l => l.Contains("state timestamp missing or without an offset", StringComparison.Ordinal));
        Assert.Single(_publisher.Telemetry);
    }

    [Fact]
    public async Task A_removal_of_a_known_device_marks_it_and_one_of_an_unknown_device_is_only_logged()
    {
        await IngestAsync(Vehicle("veh_2", At1800));
        _devices.Calls.Clear();

        await IngestAsync([new DeviceEvent.DeviceRemoved("veh_2", At1800.AddMinutes(1)), new DeviceEvent.DeviceRemoved("veh_ghost", At1800.AddMinutes(1))]);

        Assert.Equal(["removed veh_2"], _devices.Calls);
        Assert.Contains(_logs.Lines, l => l.Contains("veh_ghost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_state_gives_the_same_message_id()
    {
        await IngestAsync(Vehicle("veh_1", At1800));
        _time.Advance(TimeSpan.FromSeconds(30)); // a later poll or a redelivery: only the readings' times count
        await IngestAsync(Vehicle("veh_1", At1800));
        await IngestAsync(Vehicle("veh_1", At1800.AddMinutes(1)));

        var ids = _publisher.Telemetry.Select(m => m.MessageId).ToList();
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
    }

    [Fact]
    public async Task Each_device_is_recorded_on_its_own()
    {
        await IngestAsync([.. Vehicle("veh_1", At1800), .. Vehicle("veh_2", At1800.AddSeconds(1))]);

        Assert.Equal(2, _publisher.Telemetry.Select(m => m.DeviceId).Distinct().Count());
        Assert.All(_publisher.Telemetry, m => Assert.Single(m.Readings));
    }

    [Fact]
    public async Task The_poll_recorder_passes_the_accounts_owner_the_poll_interval_and_the_poll_time()
    {
        var recorder = new PollResultRecorder(_ingestor, _time);
        var account = new ProviderAccount("scripted", OwnerId.Parse("own_poller"), "acct_demo");

        await recorder.RecordAsync(_provider, account, TimeSpan.FromSeconds(90), new PollResult.Completed(Vehicle("veh_1", At1800)), TestContext.Current.CancellationToken);

        var (device, interval, _) = Assert.Single(_devices.Ensured);
        Assert.Equal(OwnerId.Parse("own_poller"), device.OwnerId);
        Assert.Equal(TimeSpan.FromSeconds(90), interval);
        Assert.Equal(_time.GetUtcNow(), Assert.Single(_publisher.Telemetry).OccurredAt);
    }
}
