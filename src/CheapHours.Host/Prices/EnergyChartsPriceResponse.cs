using System.Text.Json.Serialization;

namespace CheapHours.Host.Prices;

/// <summary>
/// The Energy-Charts <c>/price</c> response as sent: parallel arrays of slot starts (Unix seconds) and prices
/// (EUR/MWh, null when missing). Untrusted until <see cref="PriceResponseValidator"/> accepts it.
/// </summary>
internal sealed record EnergyChartsPriceResponse
{
    /// <summary>Gets the licence statement (CC BY 4.0, Bundesnetzagentur | SMARD.de).</summary>
    [JsonPropertyName("license_info")]
    public string? LicenseInfo { get; init; }

    /// <summary>Gets the slot starts in Unix seconds.</summary>
    [JsonPropertyName("unix_seconds")]
    public IReadOnlyList<long>? UnixSeconds { get; init; }

    /// <summary>Gets the prices, one per slot start; null for a missing price.</summary>
    [JsonPropertyName("price")]
    public IReadOnlyList<decimal?>? Price { get; init; }

    /// <summary>Gets the unit, <c>EUR / MWh</c>.</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    /// <summary>Gets whether the source marks this endpoint deprecated.</summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}
