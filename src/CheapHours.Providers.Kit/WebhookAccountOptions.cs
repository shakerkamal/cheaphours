namespace CheapHours.Providers.Kit;

/// <summary>One linked webhook account: the vendor posts its events to <c>/webhooks/{providerId}/{ExternalAccountId}</c>.</summary>
public sealed class WebhookAccountOptions
{
    /// <summary>Gets or sets the CheapHours owner id, for example <c>own_demo</c>.</summary>
    public string? Owner { get; set; }

    /// <summary>Gets or sets the vendor's id for the account; unique within the provider.</summary>
    public string? ExternalAccountId { get; set; }
}
