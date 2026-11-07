namespace CheapHours.Persistence.Idempotency;

/// <summary>What starting a request under an idempotency key gave.</summary>
public abstract record IdempotencyClaim
{
    private IdempotencyClaim()
    {
    }

    /// <summary>The request is the first with this key (or replaces an expired or abandoned one): handle it.</summary>
    public sealed record Started : IdempotencyClaim;

    /// <summary>The key was used before with the same request: replay its response.</summary>
    /// <param name="Response">The stored response.</param>
    public sealed record Replay(StoredResponse Response) : IdempotencyClaim;

    /// <summary>The key was used before with a different request.</summary>
    public sealed record Reused : IdempotencyClaim;

    /// <summary>A request with this key is still being handled.</summary>
    public sealed record InFlight : IdempotencyClaim;
}
