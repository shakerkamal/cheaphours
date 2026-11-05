using System.Text.Json;
using CheapHours.Host.Prices;

namespace CheapHours.UnitTests.Prices;

/// <summary>The recorded Energy-Charts responses for DE-LU (CC BY 4.0, Bundesnetzagentur | SMARD.de).</summary>
internal static class PriceFixtures
{
    public static readonly DateOnly SpringChange = new(2026, 3, 29);
    public static readonly DateOnly AutumnChange = new(2026, 10, 25);
    public static readonly DateOnly NormalDay = new(2026, 11, 2);

    public static IEnumerable<DateOnly> Days => [SpringChange, AutumnChange, NormalDay];

    public static string PathOf(DateOnly day) =>
        Path.Combine(AppContext.BaseDirectory, "Prices", "Fixtures", $"energy-charts-de-lu-{day:yyyy-MM-dd}.json");

    public static string Raw(DateOnly day) => File.ReadAllText(PathOf(day));

    public static EnergyChartsPriceResponse Load(DateOnly day) =>
        JsonSerializer.Deserialize<EnergyChartsPriceResponse>(Raw(day))
        ?? throw new InvalidOperationException($"Fixture for {day} is empty.");

    public static PriceDay Parsed(DateOnly day) =>
        PriceResponseValidator.Validate("DE-LU", day, Load(day)) is PriceValidation.Accepted accepted
            ? accepted.Day
            : throw new InvalidOperationException($"Fixture for {day} does not validate.");
}
