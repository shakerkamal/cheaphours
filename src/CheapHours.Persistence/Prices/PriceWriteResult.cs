namespace CheapHours.Persistence.Prices;

/// <summary>What storing price slots changed.</summary>
/// <param name="Inserted">Slots that were new.</param>
/// <param name="Modified">Slots whose price changed.</param>
public readonly record struct PriceWriteResult(int Inserted, int Modified);
