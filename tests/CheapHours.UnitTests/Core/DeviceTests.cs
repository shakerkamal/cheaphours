using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.UnitTests.Core;

public sealed class DeviceTests
{
    private static readonly DateTimeOffset _utcNoon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId _wallboxId = DeviceId.Parse("dev_wallbox_01");
    private static readonly OwnerId _owner = OwnerId.Parse("own_demo");

    [Fact]
    public void Capability_values_are_stable()
    {
        Assert.Equal(0, (int)Capabilities.None);
        Assert.Equal(1, (int)Capabilities.ReadTelemetry);
        Assert.Equal(2, (int)Capabilities.StartStop);
        Assert.Equal(4, (int)Capabilities.SetPowerLimit);
        Assert.Equal(8, (int)Capabilities.ReadStateOfCharge);
        Assert.Equal(16, (int)Capabilities.PushesEvents);
        Assert.Equal(6, Enum.GetValues<Capabilities>().Length);
    }

    [Fact]
    public void Capabilities_combine_as_flags()
    {
        var wallbox = Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.SetPowerLimit | Capabilities.PushesEvents;

        Assert.True(wallbox.HasFlag(Capabilities.SetPowerLimit));
        Assert.False(wallbox.HasFlag(Capabilities.ReadStateOfCharge));
    }

    [Fact]
    public void Reading_keeps_normalised_values()
    {
        var reading = new DeviceReading("CP-01", _utcNoon, powerW: 7400, energyWh: 12_345, socPercent: 42.5m);

        Assert.Equal("CP-01", reading.ExternalId);
        Assert.Equal(_utcNoon, reading.MeasurementTime);
        Assert.Equal(7400, reading.PowerW);
        Assert.Equal(12_345, reading.EnergyWh);
        Assert.Equal(42.5m, reading.SocPercent);
    }

    [Fact]
    public void Reading_with_non_utc_timestamp_is_refused()
    {
        var berlin = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new DeviceReading("CP-01", berlin, 0, 0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(101)]
    public void Reading_with_state_of_charge_out_of_range_is_refused(double soc)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceReading("CP-01", _utcNoon, 0, 0, (decimal)soc));
    }

    [Fact]
    public void Reading_with_negative_energy_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceReading("CP-01", _utcNoon, 0, -1));
    }

    [Fact]
    public void Reading_without_state_of_charge_reports_unknown_not_zero()
    {
        var reading = new DeviceReading("CP-01", _utcNoon, 0, 0);

        Assert.Null(reading.SocPercent);
    }

    [Fact]
    public void Reading_without_power_reports_unknown_not_zero()
    {
        var reading = new DeviceReading("CP-01", _utcNoon, powerW: null, energyWh: 12_500);

        Assert.Null(reading.PowerW);
        Assert.Equal(12_500, reading.EnergyWh);
    }

    [Fact]
    public void Hardware_without_firmware_reports_it_unknown()
    {
        var hardware = new DeviceHardware("Solidstudio", "VirtualChargePoint");

        Assert.Equal("Solidstudio", hardware.Manufacturer);
        Assert.Equal("VirtualChargePoint", hardware.Model);
        Assert.Null(hardware.FirmwareVersion);
    }

    [Theory]
    [InlineData(" ", "VirtualChargePoint")]
    [InlineData("Solidstudio", " ")]
    [InlineData("", "VirtualChargePoint")]
    public void Hardware_with_a_blank_manufacturer_or_model_is_refused(string manufacturer, string model)
    {
        Assert.Throws<ArgumentException>(() => new DeviceHardware(manufacturer, model));
    }

    [Fact]
    public void Connected_event_carries_hardware()
    {
        var hardware = new DeviceHardware("Solidstudio", "VirtualChargePoint", "1.0.0");

        var connected = new DeviceEvent.Connected("CP-01", _utcNoon, hardware);

        Assert.Equal(hardware, connected.Hardware);
        Assert.Equal("1.0.0", connected.Hardware!.FirmwareVersion);
    }

    [Fact]
    public void Connected_event_without_hardware_reports_it_unknown()
    {
        var connected = new DeviceEvent.Connected("CP-01", _utcNoon);

        Assert.Null(connected.Hardware);
    }

    [Fact]
    public void Reading_without_external_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DeviceReading(" ", _utcNoon, 0, 0));
    }

    [Fact]
    public void Charger_with_maximum_power_is_created()
    {
        var device = Charger(new DeviceAttributes(maxPowerW: 11_000));

        Assert.Equal(_wallboxId, device.Id);
        Assert.Equal(_owner, device.OwnerId);
        Assert.Equal("ocpp", device.ProviderId);
        Assert.Equal(11_000, device.Attributes.MaxPowerW);
    }

    [Fact]
    public void Charger_without_maximum_power_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Charger(new DeviceAttributes()));
    }

    [Fact]
    public void Vehicle_without_battery_capacity_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Device(
            DeviceId.Parse("dev_car_01"), _owner, "carcloud", "VIN123", DeviceType.Vehicle, Capabilities.ReadStateOfCharge, new DeviceAttributes()));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(null, 0)]
    [InlineData(-1L, null)]
    public void Non_positive_attributes_are_refused(long? batteryCapacityWh, int? maxPowerW)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceAttributes(batteryCapacityWh, maxPowerW));
    }

    [Theory]
    [InlineData("OCPP")]
    [InlineData("car-cloud")]
    [InlineData("")]
    public void Malformed_provider_id_is_refused(string providerId)
    {
        Assert.Throws<ArgumentException>(() => new Device(
            _wallboxId, _owner, providerId, "CP-01", DeviceType.Charger, Capabilities.StartStop, new DeviceAttributes(maxPowerW: 7400)));
    }

    [Fact]
    public void Default_ids_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new Device(
            default, _owner, "ocpp", "CP-01", DeviceType.Charger, Capabilities.StartStop, new DeviceAttributes(maxPowerW: 7400)));
        Assert.Throws<ArgumentException>(() => new Device(
            _wallboxId, default, "ocpp", "CP-01", DeviceType.Charger, Capabilities.StartStop, new DeviceAttributes(maxPowerW: 7400)));
    }

    [Fact]
    public void Device_created_without_a_state_is_unknown()
    {
        var device = Charger(new DeviceAttributes(maxPowerW: 11_000));

        Assert.Null(device.State);
        Assert.Equal(DeviceStatus.Unknown, device.Status);
        Assert.Equal(DeviceStatus.Unknown, device.StatusAsOf(_utcNoon));
    }

    [Fact]
    public void Device_keeps_its_state()
    {
        var reading = new DeviceReading("CP-01", _utcNoon.AddMinutes(-1), 7_000, 12_500, 40m);
        var hardware = new DeviceHardware("Solidstudio", "VCP");
        var state = new DeviceState(DeviceStatus.Charging, _utcNoon.AddMinutes(-5), _utcNoon.AddSeconds(-10), TimeSpan.FromSeconds(60), hardware, reading);

        var device = Charger(new DeviceAttributes(maxPowerW: 11_000), state);

        Assert.Same(state, device.State);
        Assert.Equal(DeviceStatus.Charging, device.Status);
        Assert.Equal(_utcNoon.AddSeconds(-10), state.LastSeenAt);
        Assert.Equal(TimeSpan.FromSeconds(60), state.ReportInterval);
        Assert.Same(hardware, state.Hardware);
        Assert.Same(reading, state.LastReading);
    }

    [Fact]
    public void State_without_optional_values_reports_them_unknown()
    {
        var state = new DeviceState(DeviceStatus.Available, _utcNoon);

        Assert.Null(state.LastSeenAt);
        Assert.Null(state.ReportInterval);
        Assert.Null(state.Hardware);
        Assert.Null(state.LastReading);
    }

    [Fact]
    public void Last_reading_of_another_device_is_refused()
    {
        var state = new DeviceState(DeviceStatus.Available, _utcNoon, lastReading: new DeviceReading("CP-02", _utcNoon, null, 100));

        Assert.Throws<ArgumentException>(() => Charger(new DeviceAttributes(maxPowerW: 11_000), state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Non_positive_report_interval_is_refused(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceState(DeviceStatus.Available, _utcNoon, reportInterval: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Non_utc_state_times_are_refused()
    {
        var berlin = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new DeviceState(DeviceStatus.Available, _utcNoon, lastSeenAt: berlin));
        Assert.Throws<ArgumentException>(() => new DeviceState(DeviceStatus.Available, berlin));
    }

    [Fact]
    public void Undefined_status_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceState((DeviceStatus)99, _utcNoon));
    }

    [Theory]
    [InlineData("Charging", 181, "Offline")] // silent past three intervals
    [InlineData("Charging", 180, "Charging")] // exactly three intervals
    [InlineData("Available", 30, "Available")]
    [InlineData("Offline", 30, "Offline")] // a stored Offline stays Offline
    public void Status_as_of_now_accounts_for_silence(string stored, int secondsSinceSeen, string expected)
    {
        var seen = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var state = new DeviceState(Enum.Parse<DeviceStatus>(stored), seen.AddMinutes(-30), seen, TimeSpan.FromSeconds(60));

        var device = Charger(new DeviceAttributes(maxPowerW: 11_000), state);

        Assert.Equal(Enum.Parse<DeviceStatus>(expected), device.StatusAsOf(seen.AddSeconds(secondsSinceSeen)));
        Assert.Equal(Enum.Parse<DeviceStatus>(stored), device.Status);
    }

    [Fact]
    public void Never_seen_device_counts_from_its_status_time()
    {
        var statusAt = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var device = Charger(new DeviceAttributes(maxPowerW: 11_000), new DeviceState(DeviceStatus.Unknown, statusAt, reportInterval: TimeSpan.FromSeconds(60)));

        Assert.Equal(DeviceStatus.Offline, device.StatusAsOf(statusAt.AddMinutes(5)));
        Assert.Equal(DeviceStatus.Unknown, device.StatusAsOf(statusAt.AddMinutes(2)));
    }

    [Fact]
    public void Device_without_report_interval_keeps_its_stored_status()
    {
        var device = Charger(new DeviceAttributes(maxPowerW: 11_000), new DeviceState(DeviceStatus.Available, _utcNoon.AddDays(-1), _utcNoon.AddDays(-1)));

        Assert.Equal(DeviceStatus.Available, device.StatusAsOf(_utcNoon));
    }

    [Fact]
    public void Status_as_of_a_non_utc_time_is_refused()
    {
        var device = Charger(new DeviceAttributes(maxPowerW: 11_000), new DeviceState(DeviceStatus.Available, _utcNoon, _utcNoon, TimeSpan.FromSeconds(60)));

        Assert.Throws<ArgumentException>(() => device.StatusAsOf(new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.FromHours(2))));
    }

    private static Device Charger(DeviceAttributes attributes, DeviceState? state = null) =>
        new(_wallboxId, _owner, "ocpp", "CP-01", DeviceType.Charger, Capabilities.StartStop | Capabilities.SetPowerLimit, attributes, state);
}
