using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppMeterValuesTests
{
    private const string ChargePoint = "CP001";
    private static readonly DateTimeOffset Received = new(2026, 10, 23, 10, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset Stamped = new(2026, 10, 23, 10, 0, 15, TimeSpan.Zero);

    private static IReadOnlyList<DeviceReading> Read(string meterValueArray) =>
        OcppMeterValues.Read(ChargePoint, JsonDocument.Parse(meterValueArray).RootElement.Clone(), Received);

    private static string Entry(string sampledValues, string timestamp = "2026-10-23T10:00:15.000Z") =>
        $$"""[{"timestamp":"{{timestamp}}","sampledValue":[{{sampledValues}}]}]""";

    private static DeviceReading Single(string sampledValues) => Assert.Single(Read(Entry(sampledValues)));

    [Fact]
    public void Simulator_energy_register_in_kWh_becomes_watt_hours()
    {
        var reading = Single("""{"value":"12.5","measurand":"Energy.Active.Import.Register","unit":"kWh"}""");

        Assert.Equal(ChargePoint, reading.ExternalId);
        Assert.Equal(Stamped, reading.MeasurementTime);
        Assert.Equal(12_500, reading.EnergyWh);
        Assert.Null(reading.PowerW);
        Assert.Null(reading.SocPercent);
    }

    [Fact]
    public void Default_measurand_and_units_apply()
    {
        var reading = Single("""{"value":"7400"},{"value":"7.4","measurand":"Power.Active.Import","unit":"kW"},{"value":"61","measurand":"SoC"}""");

        Assert.Equal(7400, reading.EnergyWh);
        Assert.Equal(7400, reading.PowerW);
        Assert.Equal(61m, reading.SocPercent);
    }

    [Fact]
    public void Power_in_watts_and_soc_in_percent_are_kept()
    {
        var reading = Single("""{"value":"100","unit":"Wh"},{"value":"3680","measurand":"Power.Active.Import","unit":"W"},{"value":"42.5","measurand":"SoC","unit":"Percent","location":"EV"}""");

        Assert.Equal(100, reading.EnergyWh);
        Assert.Equal(3680, reading.PowerW);
        Assert.Equal(42.5m, reading.SocPercent);
    }

    [Fact]
    public void Per_phase_samples_are_ignored()
    {
        var reading = Single(
            """{"value":"230","measurand":"Power.Active.Import","phase":"L1"},{"value":"231","measurand":"Power.Active.Import","phase":"L2"},""" +
            """{"value":"232","measurand":"Power.Active.Import","phase":"L3"},{"value":"5000","measurand":"Energy.Active.Import.Register"}""");

        Assert.Equal(5000, reading.EnergyWh);
        Assert.Null(reading.PowerW);
    }

    [Fact]
    public void An_entry_without_an_energy_register_gives_no_reading()
    {
        Assert.Empty(Read(Entry("""{"value":"229.8","measurand":"Voltage","unit":"V"}""")));
    }

    [Theory]
    [InlineData("""{"value":"twelve"}""")]
    [InlineData("""{"value":"-5"}""")]
    [InlineData("""{"value":"12.5","unit":"varh"}""")]
    [InlineData("""{"value":"0A1B","format":"SignedData"}""")]
    [InlineData("""{"value":"12.5","location":"Inlet"}""")]
    [InlineData("""{"value":"1e30"}""")]
    public void Unusable_energy_samples_give_no_reading(string sample)
    {
        Assert.Empty(Read(Entry(sample)));
    }

    [Theory]
    [InlineData("""{"value":"101","measurand":"SoC"}""")]
    [InlineData("""{"value":"-1","measurand":"SoC"}""")]
    [InlineData("""{"value":"50","measurand":"SoC","unit":"Wh"}""")]
    public void Unusable_soc_is_skipped_but_the_reading_stays(string soc)
    {
        var reading = Single("""{"value":"100"},""" + soc);

        Assert.Equal(100, reading.EnergyWh);
        Assert.Null(reading.SocPercent);
    }

    [Theory]
    [InlineData("""{"value":"lots","measurand":"Power.Active.Import"}""")]
    [InlineData("""{"value":"9999999999","measurand":"Power.Active.Import","unit":"kW"}""")]
    [InlineData("""{"value":"5","measurand":"Power.Active.Import","unit":"A"}""")]
    public void Unusable_power_is_skipped_but_the_reading_stays(string power)
    {
        var reading = Single("""{"value":"100"},""" + power);

        Assert.Equal(100, reading.EnergyWh);
        Assert.Null(reading.PowerW);
    }

    [Fact]
    public void Energy_is_rounded_half_away_from_zero()
    {
        Assert.Equal(13, Single("""{"value":"0.0125","unit":"kWh"}""").EnergyWh);
    }

    [Fact]
    public void The_first_usable_sample_of_a_measurand_wins()
    {
        var reading = Single("""{"value":"nope"},{"value":"200"},{"value":"300"}""");

        Assert.Equal(200, reading.EnergyWh);
    }

    [Fact]
    public void A_timestamp_after_receipt_is_replaced_by_the_receipt_time()
    {
        var reading = Assert.Single(Read(Entry("""{"value":"100"}""", timestamp: "2026-10-23T11:00:30.000Z")));

        Assert.Equal(Received, reading.MeasurementTime);
    }

    [Fact]
    public void Each_entry_gives_its_own_reading_in_order()
    {
        var readings = Read(
            """[{"timestamp":"2026-10-23T10:00:00.000Z","sampledValue":[{"value":"100"}]},""" +
            """{"timestamp":"2026-10-23T10:00:15.000Z","sampledValue":[{"value":"110"}]}]""");

        Assert.Equal([100L, 110L], readings.Select(r => r.EnergyWh));
        Assert.True(readings[0].MeasurementTime < readings[1].MeasurementTime);
    }
}
