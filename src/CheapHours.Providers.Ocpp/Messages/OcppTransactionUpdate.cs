namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// What a frame says about the charger's running transaction: it started (an accepted <c>StartTransaction</c>), it
/// is running (a <c>MeterValues</c> naming it, which covers a gateway that restarted mid-session), or it ended (a
/// <c>StopTransaction</c>). A remote stop needs the running transaction's id.
/// </summary>
/// <param name="TransactionId">The OCPP transaction id; <see langword="null"/> with <paramref name="Ended"/> when the charger reports no transaction at all (connector <c>Available</c>).</param>
/// <param name="Ended">Whether the transaction ended.</param>
public sealed record OcppTransactionUpdate(int? TransactionId, bool Ended)
{
    /// <summary>Gets the update for a connector reported <c>Available</c>: whatever ran has ended.</summary>
    public static OcppTransactionUpdate NoneRunning { get; } = new(null, Ended: true);
}
