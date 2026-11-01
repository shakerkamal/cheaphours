namespace CheapHours.Providers.Kit;

/// <summary>
/// The outcome of one vendor call made through <see cref="ProviderHttpClient{TProvider}"/>: a successful
/// response or a classified failure. Exactly two outcomes exist. Dispose it to release the response.
/// </summary>
public abstract record VendorResponse : IDisposable
{
    private VendorResponse()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this is Succeeded succeeded)
        {
            succeeded.Response.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The vendor answered with a 2xx status.</summary>
    public sealed record Succeeded : VendorResponse
    {
        /// <summary>Creates a successful outcome.</summary>
        /// <param name="response">The vendor's response; owned by this outcome.</param>
        public Succeeded(HttpResponseMessage response) => Response = response ?? throw new ArgumentNullException(nameof(response));

        /// <summary>Gets the vendor's response.</summary>
        public HttpResponseMessage Response { get; }
    }

    /// <summary>The call failed; see <see cref="Failure"/>.</summary>
    public sealed record Failed : VendorResponse
    {
        /// <summary>Creates a failed outcome.</summary>
        /// <param name="failure">The classified failure.</param>
        public Failed(VendorFailure failure) => Failure = failure ?? throw new ArgumentNullException(nameof(failure));

        /// <summary>Gets the classified failure.</summary>
        public VendorFailure Failure { get; }
    }
}
