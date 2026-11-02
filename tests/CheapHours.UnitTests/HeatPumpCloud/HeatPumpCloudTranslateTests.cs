using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;

namespace CheapHours.UnitTests.HeatPumpCloud;

/// <summary>Translating the heat-pump vendor's events (design §4 and §5).</summary>
public sealed class HeatPumpCloudTranslateTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReceivedAt = At.AddSeconds(2);

    private static IReadOnlyList<DeviceEvent> Translate(string body) =>
        HeatPumpCloudVerifyTests.Provider().Translate(new InboundMessage(null, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>(), ReceivedAt));

    private static string State(string mode = "Heating", string powerKw = "2.4", string energyKwh = "1234.5678", string online = "true", string occurredAt = "2026-11-02T10:00:00Z", string device = """{"id":"hp_1","maker":"Kaltwerk","model":"AeroHeat 9"}""") =>
        $$$"""
        {"eventId":"evt_01","type":"device.state","occurredAt":"{{{occurredAt}}}","accountId":"acct_demo",
         "device":{{{device}}},
         "state":{"online":{{{online}}},"mode":"{{{mode}}}","powerKw":{{{powerKw}}},"energyKwh":{{{energyKwh}}}}}
        """;

    [Fact]
    public void A_heating_heat_pump_is_discovered_connected_available_with_a_reading()
    {
        var events = Translate(State());

        Assert.Equal(
            [
                new DeviceEvent.Discovered("hp_1", At, DeviceType.HeatPump, new DeviceAttributes()),
                new DeviceEvent.Connected("hp_1", At, new DeviceHardware("Kaltwerk", "AeroHeat 9")),
                new DeviceEvent.StatusChanged("hp_1", At, DeviceStatus.Available),
                new DeviceEvent.ReadingReported(new DeviceReading("hp_1", At, 2_400, 1_234_568)),
            ],
            events);
    }

    [Theory]
    [InlineData("HotWater", DeviceStatus.Available)]
    [InlineData("Defrost", DeviceStatus.Available)]
    [InlineData("Standby", DeviceStatus.Available)]
    [InlineData("Fault", DeviceStatus.Faulted)]
    [InlineData("Turbo", DeviceStatus.Unknown)]
    public void Modes_map_to_statuses(string mode, DeviceStatus status)
    {
        var events = Translate(State(mode: mode, powerKw: "0"));

        Assert.Equal(status, Assert.Single(events.OfType<DeviceEvent.StatusChanged>()).Status);
        Assert.Single(events.OfType<DeviceEvent.ReadingReported>());
    }

    [Fact]
    public void An_offline_heat_pump_is_offline_with_no_reading()
    {
        var events = Translate(State(online: "false"));

        Assert.Equal(DeviceStatus.Offline, Assert.Single(events.OfType<DeviceEvent.StatusChanged>()).Status);
        Assert.Empty(events.OfType<DeviceEvent.ReadingReported>());
        Assert.Single(events.OfType<DeviceEvent.Discovered>());
    }

    [Fact]
    public void An_offset_is_converted_to_utc()
    {
        var reading = Translate(State(occurredAt: "2026-11-02T11:00:00+01:00")).OfType<DeviceEvent.ReadingReported>().Single().Reading;

        Assert.Equal(At, reading.MeasurementTime);
        Assert.Equal(TimeSpan.Zero, reading.MeasurementTime.Offset);
    }

    [Fact]
    public void A_heat_pump_without_maker_or_model_is_connected_without_hardware()
    {
        var connected = Translate(State(device: """{"id":"hp_1","maker":"Kaltwerk"}""")).OfType<DeviceEvent.Connected>().Single();

        Assert.Null(connected.Hardware);
    }

    [Fact]
    public void A_deleted_device_is_reported_removed()
    {
        var events = Translate("""{"eventId":"evt_9","type":"device.deleted","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo","device":{"id":"hp_2"}}""");

        Assert.Equal([new DeviceEvent.DeviceRemoved("hp_2", At)], events);
    }

    [Fact]
    public void An_unknown_event_type_gives_no_events()
    {
        Assert.Empty(Translate("""{"eventId":"evt_3","type":"account.updated","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo"}"""));
    }

    [Fact]
    public void A_timestamp_without_an_offset_is_malformed()
    {
        var malformed = Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(Translate(State(occurredAt: "2026-11-02T10:00:00"))));

        Assert.Equal(ReceivedAt, malformed.At);
        Assert.Contains("occurredAt", malformed.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("""{"eventId":"evt_1"}""")]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":"device.state","occurredAt":"2026-11-02T10:00:00Z"}""")]
    [InlineData("""{"type":"device.state","occurredAt":"2026-11-02T10:00:00Z","device":{"id":"  "}}""")]
    [InlineData("""{"type":"device.state","occurredAt":"2026-11-02T10:00:00Z","device":{"id":"hp_1"}}""")]
    [InlineData("""{"type":"device.state","occurredAt":"2026-11-02T10:00:00Z","device":{"id":"hp_1"},"state":{"mode":"Heating"}}""")]
    [InlineData("""{"type":"device.state","occurredAt":"2026-11-02T10:00:00Z","device":{"id":"hp_1"},"state":{"mode":"Heating","powerKw":"2.4","energyKwh":1}}""")]
    [InlineData("""{"type":"device.deleted","occurredAt":"2026-11-02T10:00:00Z"}""")]
    [InlineData("""{"type":"device.deleted","device":{"id":"hp_2"}}""")]
    public void A_malformed_body_gives_one_malformed_input(string body)
    {
        var malformed = Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(Translate(body)));

        Assert.Equal(ReceivedAt, malformed.At);
    }

    [Theory]
    [InlineData("-1", "10")]
    [InlineData("1", "-10")]
    [InlineData("99999999999999", "10")]
    [InlineData("1", "99999999999999999999")]
    public void Negative_or_huge_values_are_malformed(string powerKw, string energyKwh)
    {
        Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(Translate(State(powerKw: powerKw, energyKwh: energyKwh))));
    }

    [Fact]
    public void Translation_is_repeatable()
    {
        Assert.Equal(Translate(State()), Translate(State()));
    }
}
