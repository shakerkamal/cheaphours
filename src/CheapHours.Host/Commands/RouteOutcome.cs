using CheapHours.Core.Commands;

namespace CheapHours.Host.Commands;

/// <summary>
/// What handing a command to its route did. Exactly two outcomes exist: the private constructor stops any other
/// subtype.
/// </summary>
internal abstract record RouteOutcome
{
    private RouteOutcome()
    {
    }

    /// <summary>The route answered at once (a vendor cloud, or a failure before forwarding).</summary>
    /// <param name="Result">The send result.</param>
    public sealed record Completed(SendResult Result) : RouteOutcome;

    /// <summary>The command went to a gateway replica; its result arrives later as a <c>GatewayCommandResult</c>.</summary>
    public sealed record Forwarded : RouteOutcome;
}
