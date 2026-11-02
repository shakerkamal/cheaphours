using CheapHours.Providers.Kit;

namespace CheapHours.Providers.HeatPumpCloud;

/// <summary>
/// The heat-pump-cloud provider's configuration, section <c>Providers:HeatPumpCloud</c>. Everything comes from the
/// kit's <see cref="WebhookProviderOptions"/>: signing secret, timestamp tolerance, expected report interval and the
/// linked accounts. The kit validates it at startup; errors name the key and never contain a value. The signing
/// secret belongs in user secrets.
/// </summary>
public sealed class HeatPumpCloudOptions : WebhookProviderOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Providers:HeatPumpCloud";
}
