namespace CheapHours.Host.Api.Idempotency;

/// <summary>Opts an endpoint into idempotency keys.</summary>
internal static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Makes a repeat of the endpoint's request with the same <c>Idempotency-Key</c> get the first response instead
    /// of running again (see <see cref="IdempotencyMiddleware"/>).
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder type.</typeparam>
    /// <param name="builder">The endpoint builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder Idempotent<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(IdempotentMetadata.Instance);
}
