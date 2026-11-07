namespace CheapHours.Host.Api.Idempotency;

/// <summary>Marks an endpoint whose requests may carry an <c>Idempotency-Key</c> (design §10).</summary>
internal sealed class IdempotentMetadata
{
    /// <summary>The one instance.</summary>
    public static readonly IdempotentMetadata Instance = new();

    private IdempotentMetadata()
    {
    }
}
