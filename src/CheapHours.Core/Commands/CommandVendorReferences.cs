namespace CheapHours.Core.Commands;

/// <summary>Vendor references with a meaning to CheapHours itself, carried on <see cref="SendResult.Accepted"/>.</summary>
public static class CommandVendorReferences
{
    /// <summary>
    /// The route found the device already in the requested state and sent nothing (a start for a charger
    /// that is charging, a stop for one that is not). The command is then confirmed from the latest report.
    /// </summary>
    public const string AlreadyInEffect = "already-in-effect";
}
