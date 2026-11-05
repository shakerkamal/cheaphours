namespace CheapHours.Persistence.Locks;

/// <summary>The names of the worker's leases; each is registered as a keyed <see cref="ILease"/>.</summary>
public static class LeaseNames
{
    /// <summary>The polling leader: one replica polls vendor clouds.</summary>
    public const string DevicePolling = "device-polling";

    /// <summary>One replica imports day-ahead prices per run.</summary>
    public const string PriceImport = "price-import";

    /// <summary>Gets every lease name.</summary>
    public static IReadOnlyList<string> All { get; } = [DevicePolling, PriceImport];
}
