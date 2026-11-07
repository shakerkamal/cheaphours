namespace CheapHours.Persistence.Idempotency;

/// <summary>Where a request under an idempotency key is.</summary>
public enum IdempotencyState
{
    /// <summary>The first request is being handled.</summary>
    InProgress,

    /// <summary>The first request finished with a response below 500, which is kept for replay.</summary>
    Completed,
}
