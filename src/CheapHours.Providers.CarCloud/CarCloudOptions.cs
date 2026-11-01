using CheapHours.Providers.Kit;

namespace CheapHours.Providers.CarCloud;

/// <summary>
/// The car-cloud provider's configuration, section <c>Providers:CarCloud</c>. Everything comes from the kit's
/// <see cref="OAuthProviderOptions"/>: base address, token endpoint, client id and secret, poll interval, rate
/// limit, HTTP pipeline and the seeded accounts. The kit validates it at startup; errors name the key and never
/// contain a value. The client secret and seed refresh tokens belong in user secrets.
/// </summary>
public sealed class CarCloudOptions : OAuthProviderOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Providers:CarCloud";
}
