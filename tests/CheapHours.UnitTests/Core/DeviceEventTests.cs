using CheapHours.Core.Devices;

namespace CheapHours.UnitTests.Core;

public sealed class DeviceEventTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Vehicle_discovered_with_battery_capacity()
    {
        var discovered = new DeviceEvent.Discovered("veh_1", At, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 77_000));

        Assert.Equal("veh_1", discovered.ExternalId);
        Assert.Equal(At, discovered.At);
        Assert.Equal(DeviceType.Vehicle, discovered.Type);
        Assert.Equal(77_000, discovered.Attributes.BatteryCapacityWh);
    }

    [Fact]
    public void Vehicle_without_battery_capacity_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => new DeviceEvent.Discovered("veh_1", At, DeviceType.Vehicle, new DeviceAttributes()));

        Assert.Equal("attributes", error.ParamName);
    }

    [Fact]
    public void Charger_without_maximum_power_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DeviceEvent.Discovered("CP001", At, DeviceType.Charger, new DeviceAttributes()));
    }

    [Fact]
    public void Time_must_be_utc()
    {
        var local = new DateTimeOffset(2026, 10, 28, 19, 0, 0, TimeSpan.FromHours(1));

        Assert.Throws<ArgumentException>(() => new DeviceEvent.Discovered("veh_1", local, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 77_000)));
    }

    [Fact]
    public void External_id_must_not_be_blank()
    {
        Assert.Throws<ArgumentException>(() => new DeviceEvent.Discovered(" ", At, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: 77_000)));
    }
}
