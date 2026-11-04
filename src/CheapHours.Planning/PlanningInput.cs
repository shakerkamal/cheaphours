namespace CheapHours.Planning;

/// <summary>
/// Everything the planner needs: the time, the goal, the two devices' limits and the known prices. The planner
/// reads nothing else, so the same input always gives the same plan.
/// </summary>
public sealed record PlanningInput
{
    /// <summary>Creates a planning input.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <param name="departureUtc">When the car must be ready (UTC).</param>
    /// <param name="startSocPercent">The vehicle's current charge, 0–100 percent.</param>
    /// <param name="targetSocPercent">The target charge, 1–100 percent.</param>
    /// <param name="batteryCapacityWh">The battery's usable capacity in Wh; greater than zero.</param>
    /// <param name="chargerMaxPowerW">The charger's maximum power in W; greater than zero.</param>
    /// <param name="pricesEurPerMwh">Known prices by UTC slot start; slots without an entry have no price yet.</param>
    /// <param name="safetyMargin">How long before departure charging must end; zero or more.</param>
    /// <param name="chargingEfficiency">The share of grid energy that reaches the battery, 0.5–1.0.</param>
    /// <exception cref="ArgumentException">A value is missing or out of range.</exception>
    public PlanningInput(
        DateTimeOffset nowUtc,
        DateTimeOffset departureUtc,
        decimal startSocPercent,
        int targetSocPercent,
        long batteryCapacityWh,
        int chargerMaxPowerW,
        IReadOnlyDictionary<DateTimeOffset, decimal> pricesEurPerMwh,
        TimeSpan safetyMargin,
        decimal chargingEfficiency)
    {
        ArgumentNullException.ThrowIfNull(pricesEurPerMwh);
        RequireUtc(nowUtc, nameof(nowUtc));
        RequireUtc(departureUtc, nameof(departureUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(startSocPercent, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startSocPercent, 100m);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetSocPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetSocPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batteryCapacityWh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chargerMaxPowerW);
        ArgumentOutOfRangeException.ThrowIfLessThan(safetyMargin, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(chargingEfficiency, 0.5m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chargingEfficiency, 1.0m);

        NowUtc = nowUtc;
        DepartureUtc = departureUtc;
        StartSocPercent = startSocPercent;
        TargetSocPercent = targetSocPercent;
        BatteryCapacityWh = batteryCapacityWh;
        ChargerMaxPowerW = chargerMaxPowerW;
        PricesEurPerMwh = pricesEurPerMwh;
        SafetyMargin = safetyMargin;
        ChargingEfficiency = chargingEfficiency;
    }

    /// <summary>Gets the current time (UTC).</summary>
    public DateTimeOffset NowUtc { get; }

    /// <summary>Gets the departure (UTC).</summary>
    public DateTimeOffset DepartureUtc { get; }

    /// <summary>Gets the vehicle's current charge in percent.</summary>
    public decimal StartSocPercent { get; }

    /// <summary>Gets the target charge in percent.</summary>
    public int TargetSocPercent { get; }

    /// <summary>Gets the battery capacity in Wh.</summary>
    public long BatteryCapacityWh { get; }

    /// <summary>Gets the charger's maximum power in W.</summary>
    public int ChargerMaxPowerW { get; }

    /// <summary>Gets the known prices by UTC slot start, in EUR/MWh.</summary>
    public IReadOnlyDictionary<DateTimeOffset, decimal> PricesEurPerMwh { get; }

    /// <summary>Gets the safety margin before departure.</summary>
    public TimeSpan SafetyMargin { get; }

    /// <summary>Gets the charging efficiency.</summary>
    public decimal ChargingEfficiency { get; }

    private static void RequireUtc(DateTimeOffset value, string paramName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Must be UTC (offset 00:00), but the offset is {value.Offset}.", paramName);
        }
    }
}
