using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// <remarks>
    /// When <typeparamref name="TOptions"/> derives from <see cref="HttpProviderOptions"/>, the provider also gets
    /// its own named <see cref="HttpClient"/> with the vendor resilience pipeline (the bulkhead between
    /// providers), <see cref="ProviderHttpClient{TProvider}"/> with per-account rate limiting and metrics, and a
    /// <see cref="ProviderPollingSettings"/> entry. When it derives from <see cref="WebhookProviderOptions"/>, the
    /// provider's linked accounts are added to <see cref="WebhookAccountDirectory"/>. The options are validated at
    /// startup without echoing values.
    /// </remarks>
    public static IServiceCollection AddProvider<TProvider, TOptions>(this IServiceCollection services, string configSection)
        where TProvider : class, IDeviceProvider
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSection);

        services.AddOptions<TOptions>().BindConfiguration(configSection).ValidateOnStart();
        if (typeof(HttpProviderOptions).IsAssignableFrom(typeof(TOptions)))
        {
            AddHttp<TProvider, TOptions>(services, configSection);
        }

        if (typeof(WebhookProviderOptions).IsAssignableFrom(typeof(TOptions)))
        {
            AddWebhook<TProvider, TOptions>(services, configSection);
        }

        return services.AddProvider<TProvider>(configSection);
    }

    private static void AddWebhook<TProvider, TOptions>(IServiceCollection services, string configSection)
        where TProvider : class, IDeviceProvider
        where TOptions : class
    {
        services.AddSingleton<IValidateOptions<TOptions>>(new WebhookProviderOptionsValidator<TOptions>(configSection));
        services.AddSingleton(new WebhookRegistration(
            typeof(TProvider),
            sp => (WebhookProviderOptions)(object)sp.GetRequiredService<IOptions<TOptions>>().Value));
        services.TryAddSingleton(sp => new WebhookAccountDirectory(sp.GetServices<WebhookRegistration>(), sp));
    }

    private static void AddHttp<TProvider, TOptions>(IServiceCollection services, string configSection)
        where TProvider : class, IDeviceProvider
        where TOptions : class
    {
        static HttpProviderOptions Options(IServiceProvider sp) => (HttpProviderOptions)(object)sp.GetRequiredService<IOptions<TOptions>>().Value;

        services.AddSingleton<IValidateOptions<TOptions>>(new HttpProviderOptionsValidator<TOptions>(configSection));
        services.AddProviderMetrics();
        services.AddSingleton(new CircuitRegistration(typeof(TProvider)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AccountRateLimiter>();

        // The resilience registry caches the pipeline per provider, so every client of the provider shares one breaker.
        services.AddHttpClient(typeof(TProvider).FullName!, (sp, client) =>
            {
                client.BaseAddress = Options(sp).BaseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan; // the pipeline's timeouts govern
            })
            .AddResilienceHandler("vendor", (pipeline, context) => VendorPipeline.Configure(
                pipeline,
                Options(context.ServiceProvider).Http,
                state => context.ServiceProvider.GetRequiredService<ProviderCircuitStates>().Set(typeof(TProvider), state)));

        var isOAuth = typeof(OAuthProviderOptions).IsAssignableFrom(typeof(TOptions));
        var tokenClientName = typeof(TProvider).FullName + ".token";
        if (isOAuth)
        {
            // Token refreshes get their own client: a timeout, no retry and no breaker.
            services.AddHttpClient(tokenClientName, (sp, client) => client.Timeout = Options(sp).Http.TotalTimeout);
            services.TryAddSingleton<TokenProtector>();
            services.AddSingleton(new OAuthRegistration(typeof(TProvider), sp => (OAuthProviderOptions)Options(sp)));
            services.TryAddSingleton(sp => new ProviderAccountSeeder(
                sp.GetServices<OAuthRegistration>(),
                sp,
                sp.GetRequiredService<IProviderAccountRepository>(),
                sp.GetRequiredService<TokenProtector>(),
                sp.GetRequiredService<ILogger<ProviderAccountSeeder>>()));
        }

        services.TryAddSingleton(sp => new ProviderHttpClient<TProvider>(
            sp.GetRequiredService<IHttpClientFactory>(),
            Options(sp),
            sp.GetRequiredService<AccountRateLimiter>(),
            sp.GetRequiredService<ProviderMetrics>(),
            sp.GetRequiredService<TimeProvider>(),
            isOAuth
                ? new OAuthTokenStore(
                    (OAuthProviderOptions)Options(sp),
                    tokenClientName,
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IProviderAccountRepository>(),
                    sp.GetRequiredService<TokenProtector>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<OAuthTokenStore>>())
                : null));

        services.AddSingleton(sp =>
        {
            var options = Options(sp);
            return new ProviderPollingSettings(typeof(TProvider), options.PollInterval, options.Http.TotalTimeout);
        });
    }

    /// <summary>
    /// Registers the vendor-call instruments on the <c>CheapHours.Providers</c> meter (<see cref="ProviderMetrics"/>)
    /// and the circuit-breaker states they report. <c>AddProvider</c> does this for HTTP providers; a host that calls
    /// devices another way (the OCPP gateway) calls it to record those calls on the same instruments.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProviderMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMetrics();
        services.TryAddSingleton(sp => new ProviderCircuitStates(sp.GetServices<CircuitRegistration>(), sp));
        services.TryAddSingleton<ProviderMetrics>();
        return services;
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
