using CheapHours.Core.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Looks up the owner of a linked webhook account, and a webhook provider's expected report interval, from the
/// configured <see cref="WebhookProviderOptions"/> of every webhook provider registration. Provider ids are read
/// from the provider instances on first use. Registered only when a webhook provider is.
/// </summary>
public sealed class WebhookAccountDirectory
{
    private readonly Lazy<Dictionary<string, Entry>> _entries;

    internal WebhookAccountDirectory(IEnumerable<WebhookRegistration> registrations, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(services);
        _entries = new Lazy<Dictionary<string, Entry>>(() => Build(registrations, services), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Returns the owner of a linked account.</summary>
    /// <param name="providerId">The provider id from the callback URL.</param>
    /// <param name="externalAccountId">The vendor's account id from the callback URL.</param>
    /// <returns>The owner, or <see langword="null"/> when the provider has no such account (compared exactly).</returns>
    public OwnerId? FindOwner(string providerId, string externalAccountId) =>
        _entries.Value.TryGetValue(providerId, out var entry) && entry.Owners.TryGetValue(externalAccountId, out var owner) ? owner : null;

    /// <summary>Returns how often a webhook provider's vendor reports each device.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The expected report interval, or <see langword="null"/> when the id names no webhook provider.</returns>
    public TimeSpan? ReportInterval(string providerId) =>
        _entries.Value.TryGetValue(providerId, out var entry) ? entry.ReportInterval : null;

    private static Dictionary<string, Entry> Build(IEnumerable<WebhookRegistration> registrations, IServiceProvider services)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var provider = (IDeviceProvider)services.GetRequiredService(registration.ProviderType);
            var options = registration.Options(services);
            var owners = options.Accounts.ToDictionary(a => a.ExternalAccountId!, a => OwnerId.Parse(a.Owner!), StringComparer.Ordinal);
            entries[provider.ProviderId] = new Entry(owners, options.ExpectedReportInterval);
        }

        return entries;
    }

    private sealed record Entry(Dictionary<string, OwnerId> Owners, TimeSpan ReportInterval);
}
