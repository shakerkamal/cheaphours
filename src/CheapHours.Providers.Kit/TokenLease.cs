namespace CheapHours.Providers.Kit;

/// <summary>An access token to use for one call, or why there is none.</summary>
/// <param name="AccessToken">The access token, or <see langword="null"/> on failure.</param>
/// <param name="Version">The stored version the token belongs to; passed back after a 401.</param>
/// <param name="Failure">Why no token could be had, or <see langword="null"/>.</param>
internal readonly record struct TokenLease(string? AccessToken, long Version, VendorFailure? Failure)
{
    public static TokenLease Failed(VendorFailure failure) => new(null, 0, failure);

    /// <summary>Keeps the token out of logs.</summary>
    /// <returns>The version and failure only.</returns>
    public override string ToString() => $"TokenLease {{ Version = {Version}, Failure = {Failure?.Reason} }}";
}
