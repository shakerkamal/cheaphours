using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CheapHours.Providers.Kit;

/// <summary>Registers device providers: one line per provider plus its configuration section.</summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as a singleton under <see cref="IDeviceProvider"/> and every
    /// interaction interface it implements. Startup fails if the provider id is malformed or taken, the
    /// configuration section does not exist, or the declared capabilities do not match the interfaces.
    /// </summary>
    /// <typeparam name="TProvider">The provider implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configSection">The provider's configuration section, for example <c>Providers:CarCloud</c>.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProvider<TProvider>(this IServiceCollection services, string configSection)
        where TProvider : class, IDeviceProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSection);

        services.TryAddSingleton<TProvider>();
        services.AddSingleton<IDeviceProvider>(sp => sp.GetRequiredService<TProvider>());
        AddAs<TProvider, IPollingProvider>(services);
        AddAs<TProvider, IInboundTranslator>(services);
        AddAs<TProvider, ICommandSender>(services);
        AddAs<TProvider, IWebhookVerifier>(services);

        services.AddSingleton(new ProviderRegistration(typeof(TProvider), configSection));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProviderRegistryValidator>());
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as <see cref="AddProvider{TProvider}"/> does, and binds
    /// <typeparamref name="TOptions"/> to the configuration section, validated at startup.
    /// </summary>
    /// <typeparam name="TProvider">The provider implementation.</typeparam>
    /// <typeparam name="TOptions">The provider's options type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configSection">The provider's configuration section.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProvider<TProvider, TOptions>(this IServiceCollection services, string configSection)
        where TProvider : class, IDeviceProvider
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSection);

        services.AddOptions<TOptions>().BindConfiguration(configSection).ValidateOnStart();
        return services.AddProvider<TProvider>(configSection);
    }

    private static void AddAs<TProvider, TInterface>(IServiceCollection services)
        where TProvider : class, IDeviceProvider
        where TInterface : class, IDeviceProvider
    {
        if (typeof(TInterface).IsAssignableFrom(typeof(TProvider)))
        {
            services.AddSingleton<TInterface>(sp => (TInterface)(object)sp.GetRequiredService<TProvider>());
        }
    }
}
