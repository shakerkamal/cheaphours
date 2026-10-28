using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Gateway;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheapHours.UnitTests.Host;

public sealed class DeviceEventRecorderTests
{
    private const string Provider = "ocpp";
    private const string ChargePoint = "CP001";
    private static readonly DateTimeOffset Received = new(2026, 10, 23, 10, 0, 30, TimeSpan.Zero);
    private static readonly DeviceId Device = DeviceId.Parse("dev_wallbox_01");

    private readonly List<string> _calls = [];
    private readonly RecordingDevices _devices;
    private readonly RecordingPublisher _publisher;
    private readonly DeviceEventRecorder _recorder;

    public DeviceEventRecorderTests()
    {
        _devices = new RecordingDevices(_calls);
        _publisher = new RecordingPublisher(_calls);
        _recorder = new DeviceEventRecorder(_devices, _publisher, NullLogger<DeviceEventRecorder>.Instance);
    }

    private Task RecordAsync(params DeviceEvent[] events) =>
        _recorder.RecordAsync(Provider, ChargePoint, Device, events, Received, TestContext.Current.CancellationToken);

    private static DeviceEvent.ReadingReported Reading(int seconds, long energyWh) =>
        new(new DeviceReading(ChargePoint, Received.AddSeconds(-seconds), null, energyWh));

    [Fact]
    public async Task Hardware_on_a_connection_is_stored()
    {
        var hardware = new DeviceHardware("Solidstudio", "VirtualChargePoint", "1.0.0");

        await RecordAsync(new DeviceEvent.Connected(ChargePoint, Received, hardware));

        Assert.Equal(["hardware CP001 Solidstudio"], _calls);
    }

    [Fact]
    public async Task A_connection_without_hardware_stores_nothing()
    {
        await RecordAsync(new DeviceEvent.Connected(ChargePoint, Received));

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_status_change_is_stored_with_its_time()
    {
        await RecordAsync(new DeviceEvent.StatusChanged(ChargePoint, Received.AddSeconds(-5), DeviceStatus.Charging));

        Assert.Equal([$"status CP001 Charging {Received.AddSeconds(-5):O}"], _calls);
    }

    [Fact]
    public async Task All_readings_of_a_frame_go_out_as_one_message_in_frame_order()
    {
        await RecordAsync(Reading(30, 100), Reading(15, 110), Reading(0, 120));

        var message = Assert.IsType<TelemetryReceived>(Assert.Single(_publisher.Published));
        Assert.Equal(Device, message.DeviceId);
        Assert.Equal(Provider, message.ProviderId);
        Assert.Equal(Received, message.OccurredAt);
        Assert.Null(message.Traceparent);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.Equal([100L, 110L, 120L], message.Readings.Select(r => r.EnergyWh));
    }

    [Fact]
    public async Task Store_writes_come_before_the_publish()
    {
        await RecordAsync(Reading(0, 120), new DeviceEvent.StatusChanged(ChargePoint, Received, DeviceStatus.Charging));

        Assert.Equal(2, _calls.Count);
        Assert.StartsWith("status", _calls[0], StringComparison.Ordinal);
        Assert.Equal("publish TelemetryReceived", _calls[1]);
    }

    [Fact]
    public async Task Frames_without_readings_publish_nothing()
    {
        await RecordAsync(new DeviceEvent.StatusChanged(ChargePoint, Received, DeviceStatus.Available), new DeviceEvent.MalformedInput(Received, "no usable meter value"));

        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Malformed_input_is_only_logged()
    {
        await RecordAsync(new DeviceEvent.MalformedInput(Received, "no usable meter value"));

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_failed_publish_reaches_the_caller()
    {
        _publisher.Fail = true;

        await Assert.ThrowsAsync<BrokerUnavailableException>(() => RecordAsync(Reading(0, 120)));
    }

    private sealed class RecordingPublisher(List<string> calls) : IMessagePublisher
    {
        public List<Message> Published { get; } = [];

        public bool Fail { get; set; }

        public Task PublishAsync(Message message, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new BrokerUnavailableException("localhost:5672");
            }

            Published.Add(message);
            calls.Add($"publish {message.MessageType}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDevices(List<string> calls) : IDeviceStateStore
    {
        public Task<DeviceId> EnsureAsync(Device device, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordHardwareAsync(string providerId, string externalId, DeviceHardware hardware, CancellationToken cancellationToken)
        {
            calls.Add($"hardware {externalId} {hardware.Manufacturer}");
            return Task.CompletedTask;
        }

        public Task<bool> RecordStatusAsync(string providerId, string externalId, DeviceStatus status, DateTimeOffset at, CancellationToken cancellationToken)
        {
            calls.Add($"status {externalId} {status} {at:O}");
            return Task.FromResult(true);
        }

        public Task<bool> MarkOfflineAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordSeenAsync(string providerId, string externalId, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeviceState?> FindAsync(string providerId, string externalId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
