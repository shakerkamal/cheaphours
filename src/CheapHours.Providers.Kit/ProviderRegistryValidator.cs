using CheapHours.Core.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.Providers.Kit;

/// <summary>
/// Fails startup when provider registrations are inconsistent, reporting every problem at once instead of
/// failing later on the first device that hits one.
/// </summary>
internal sealed class ProviderRegistryValidator(
    IEnumerable<ProviderRegistration> registrations,
    IConfiguration configuration,
    IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var problems = FindProblems().ToArray();
        if (problems.Length > 0)
        {
            throw new InvalidOperationException(
                "Invalid device provider registrations:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<string> FindProblems()
    {
        var seenIds = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            var provider = (IDeviceProvider)services.GetRequiredService(registration.ProviderType);
            var name = $"{registration.ProviderType.Name} ('{provider.ProviderId}')";

            if (!ProviderIdFormat.IsValid(provider.ProviderId))
            {
                yield return $"{name}: provider id must be 1–{ProviderIdFormat.MaxLength} lowercase letters or digits.";
            }
            else if (!seenIds.TryAdd(provider.ProviderId, registration.ProviderType))
            {
                yield return $"{name}: provider id '{provider.ProviderId}' is already used by {seenIds[provider.ProviderId].Name}.";
            }

            if (!configuration.GetSection(registration.ConfigSection).Exists())
            {
                yield return $"{name}: configuration section '{registration.ConfigSection}' does not exist.";
            }

            if (provider is ICommandSender && (provider.Capabilities & (Capabilities.StartStop | Capabilities.SetPowerLimit)) == 0)
            {
                yield return $"{name}: implements {nameof(ICommandSender)} but declares neither {nameof(Capabilities.StartStop)} nor {nameof(Capabilities.SetPowerLimit)}.";
            }

            if (provider is IInboundTranslator && !provider.Capabilities.HasFlag(Capabilities.PushesEvents))
            {
                yield return $"{name}: implements {nameof(IInboundTranslator)} but does not declare {nameof(Capabilities.PushesEvents)}.";
            }
        }
    }
}
