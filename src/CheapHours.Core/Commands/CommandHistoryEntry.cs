namespace CheapHours.Core.Commands;

/// <summary>One state a command entered, when, and why.</summary>
public sealed record CommandHistoryEntry
{
    /// <summary>Creates an entry.</summary>
    /// <param name="state">The state the command entered.</param>
    /// <param name="at">When, in UTC.</param>
    /// <param name="vendorReference">The vendor's reference for the request, if the vendor gave one.</param>
    /// <param name="reason">Why the command entered this state, if known (for example a vendor error).</param>
    /// <exception cref="ArgumentException"><paramref name="at"/> is not UTC.</exception>
    public CommandHistoryEntry(CommandState state, DateTimeOffset at, string? vendorReference = null, string? reason = null)
    {
        State = state;
        At = Guard.Utc(at);
        VendorReference = vendorReference;
        Reason = reason;
    }

    /// <summary>Gets the state the command entered.</summary>
    public CommandState State { get; }

    /// <summary>Gets when the command entered the state (UTC).</summary>
    public DateTimeOffset At { get; }

    /// <summary>Gets the vendor's reference for the request, if any.</summary>
    public string? VendorReference { get; }

    /// <summary>Gets why the command entered this state, if known.</summary>
    public string? Reason { get; }
}
