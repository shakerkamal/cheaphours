using System.Globalization;
using System.Text.Json;
using CheapHours.Core.Devices;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// Reads OCPP 1.6 <c>meterValue</c> entries (from <c>MeterValues</c> or a stop's <c>transactionData</c>)
/// into normalised readings. Pure, and total on schema-valid input: an unusable sample is skipped, never
/// thrown on.
/// </summary>
/// <remarks>
/// Only phase-less <c>Raw</c> samples count. Energy and power must be measured at the outlet (or not say
/// where); state of charge may come from anywhere, usually the EV. The first usable sample of each
/// measurand wins. An entry gives a reading only when it has a usable energy register.
/// </remarks>
public static class OcppMeterValues
{
    private const string EnergyRegister = "Energy.Active.Import.Register";
    private const string Power = "Power.Active.Import";
    private const string StateOfCharge = "SoC";

    /// <summary>Reads every entry of a <c>meterValue</c> array.</summary>
    /// <param name="chargePointId">The charger the values came from.</param>
    /// <param name="meterValues">The schema-valid <c>meterValue</c> array.</param>
    /// <param name="receivedAt">When the frame was received (UTC); bounds each entry's time.</param>
    /// <returns>One reading per entry with a usable energy register, in entry order.</returns>
    public static IReadOnlyList<DeviceReading> Read(string chargePointId, JsonElement meterValues, DateTimeOffset receivedAt)
    {
        var readings = new List<DeviceReading>();
        foreach (var entry in meterValues.EnumerateArray())
        {
            if (ReadEntry(chargePointId, entry, receivedAt) is { } reading)
            {
                readings.Add(reading);
            }
        }

        return readings;
    }

    /// <summary>A reading of the energy register alone, as a transaction's meter start or stop reports it.</summary>
    /// <param name="chargePointId">The charger.</param>
    /// <param name="energyWh">The register value in Wh.</param>
    /// <param name="reportedTimestamp">The message's <c>timestamp</c>.</param>
    /// <param name="receivedAt">When the frame was received (UTC).</param>
    /// <returns>The reading, or <see langword="null"/> when the value is negative.</returns>
    public static DeviceReading? EnergyOnly(string chargePointId, long energyWh, string? reportedTimestamp, DateTimeOffset receivedAt) =>
        energyWh < 0
            ? null
            : new DeviceReading(chargePointId, OcppStatusMapping.ReportedTime(reportedTimestamp, receivedAt), powerW: null, energyWh);

    private static DeviceReading? ReadEntry(string chargePointId, JsonElement entry, DateTimeOffset receivedAt)
    {
        long? energyWh = null;
        int? powerW = null;
        decimal? socPercent = null;

        foreach (var sample in entry.GetProperty("sampledValue").EnumerateArray())
        {
            if (!IsUsable(sample) || !TryValue(sample, out var value))
            {
                continue;
            }

            var unit = Text(sample, "unit");
            switch (Text(sample, "measurand") ?? EnergyRegister)
            {
                case EnergyRegister when energyWh is null && AtOutlet(sample):
                    energyWh = Energy(value, unit);
                    break;
                case Power when powerW is null && AtOutlet(sample):
                    powerW = PowerW(value, unit);
                    break;
                case StateOfCharge when socPercent is null:
                    socPercent = Soc(value, unit);
                    break;
            }
        }

        if (energyWh is not { } energy)
        {
            return null;
        }

        var at = OcppStatusMapping.ReportedTime(Text(entry, "timestamp"), receivedAt);
        return new DeviceReading(chargePointId, at, powerW, energy, socPercent);
    }

    private static bool IsUsable(JsonElement sample) =>
        Text(sample, "format") is null or "Raw" && Text(sample, "phase") is null;

    private static bool AtOutlet(JsonElement sample) => Text(sample, "location") is null or "Outlet";

    private static bool TryValue(JsonElement sample, out decimal value) =>
        decimal.TryParse(Text(sample, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static long? Energy(decimal value, string? unit)
    {
        var wh = unit switch
        {
            null or "Wh" => Scale(value, 1m),
            "kWh" => Scale(value, 1000m),
            _ => null,
        };

        return wh is { } w && w >= 0 && w <= long.MaxValue ? (long)decimal.Round(w, MidpointRounding.AwayFromZero) : null;
    }

    private static int? PowerW(decimal value, string? unit)
    {
        var w = unit switch
        {
            null or "W" => Scale(value, 1m),
            "kW" => Scale(value, 1000m),
            _ => null,
        };

        return w is { } p && p >= int.MinValue && p <= int.MaxValue ? (int)decimal.Round(p, MidpointRounding.AwayFromZero) : null;
    }

    private static decimal? Soc(decimal value, string? unit) =>
        unit is null or "Percent" && value is >= 0 and <= 100 ? value : null;

    // Values beyond any real meter are dropped before scaling, so the multiplication cannot overflow.
    private static decimal? Scale(decimal value, decimal factor) =>
        Math.Abs(value) <= decimal.MaxValue / factor ? value * factor : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
