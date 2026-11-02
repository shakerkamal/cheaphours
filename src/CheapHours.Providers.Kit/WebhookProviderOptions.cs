namespace CheapHours.Providers.Kit;

/// <summary>
/// Options for a provider whose vendor calls us with signed webhooks. A provider whose options type derives from
/// this gets, from <see cref="ProviderServiceCollectionExtensions.AddProvider{TProvider, TOptions}"/>, startup
/// validation that never echoes a value, and its accounts in <see cref="WebhookAccountDirectory"/>. The signing
/// secret belongs in user secrets, never in a committed file.
/// </summary>
public class WebhookProviderOptions
{
    /// <summary>The shortest signing secret allowed.</summary>
    public const int MinimumSecretLength = 32;

    /// <summary>The smallest timestamp tolerance allowed.</summary>
    public static readonly TimeSpan MinimumTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The largest timestamp tolerance allowed.</summary>
    public static readonly TimeSpan MaximumTolerance = TimeSpan.FromMinutes(15);

    /// <summary>The shortest expected report interval allowed.</summary>
    public static readonly TimeSpan MinimumReportInterval = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the secret the vendor signs webhooks with; required, at least 32 characters, never logged.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Gets or sets how far a signature's timestamp may be from the time of receipt (1 s – 15 min).</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets how often the vendor reports each device (at least 5 s); the report interval of discovered devices.</summary>
    public TimeSpan ExpectedReportInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the linked accounts: each vendor account is given its own callback URL and belongs to one owner (D3).</summary>
    public IList<WebhookAccountOptions> Accounts { get; } = [];
}
