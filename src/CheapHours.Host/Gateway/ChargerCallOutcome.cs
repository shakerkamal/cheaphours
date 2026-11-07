using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Host.Gateway;

/// <summary>
/// What became of a call the gateway sent to a charger. Exactly three outcomes exist: the private constructor
/// stops any other subtype.
/// </summary>
internal abstract record ChargerCallOutcome
{
    private ChargerCallOutcome()
    {
    }

    /// <summary>The charger answered with a <c>CallResult</c> or a <c>CallError</c>.</summary>
    /// <param name="UniqueId">The call's unique id.</param>
    /// <param name="Reply">The answer.</param>
    /// <param name="ReceivedAt">When the gateway received the answer (UTC), before any frame the charger sent after it.</param>
    public sealed record Replied(string UniqueId, OcppFrame Reply, DateTimeOffset ReceivedAt) : ChargerCallOutcome;

    /// <summary>No answer arrived within the call timeout; the connection stays open.</summary>
    public sealed record TimedOut : ChargerCallOutcome;

    /// <summary>The connection closed before an answer arrived, or the call could not be written.</summary>
    public sealed record Closed : ChargerCallOutcome;
}
