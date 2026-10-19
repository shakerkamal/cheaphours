namespace CheapHours.Host.Api.Dtos;

/// <summary>The day-ahead price of one 15-minute slot.</summary>
/// <param name="Zone">The bidding zone, for example <c>DE-LU</c>.</param>
/// <param name="Start">Slot start (UTC).</param>
/// <param name="End">Slot end (UTC).</param>
/// <param name="PriceEurPerMwh">The exchange price in EUR/MWh; can be negative.</param>
internal sealed record PriceSlotDto(string Zone, DateTimeOffset Start, DateTimeOffset End, decimal PriceEurPerMwh);

/// <summary>Savings over a period, against charging on arrival.</summary>
/// <param name="From">Period start (UTC).</param>
/// <param name="To">Period end (UTC).</param>
/// <param name="Sessions">Charging sessions in the period.</param>
/// <param name="PlannedCostEur">What the confirmed plans cost, in EUR (exchange price only).</param>
/// <param name="ArrivalCostEur">What charging on arrival would have cost, in EUR.</param>
/// <param name="SavingsEur">The difference, in EUR.</param>
internal sealed record SavingsDto(DateTimeOffset From, DateTimeOffset To, int Sessions, decimal PlannedCostEur, decimal ArrivalCostEur, decimal SavingsEur);
