using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Api.Auth;

/// <summary>Registers API-key authentication and the scope policies.</summary>
internal static class ApiKeyAuthenticationSetup
{
    /// <summary>Adds the <c>ApiKey</c> scheme, the MongoDB key store, startup validation of configured keys and the <c>scope:read</c>/<c>scope:execute</c> policies.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (section <c>ApiKeys</c>).</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Configured keys are checked at startup, before MongoDB is needed; ApiStartup seeds them.
        services.AddOptions<ApiKeyOptions>().Bind(configuration.GetSection(ApiKeyOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>());
        services.TryAddSingleton<IApiKeyStore, MongoApiKeyStore>();

        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiScopes.ReadPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(ApiScopes.ClaimType, ApiScopes.Read))
            .AddPolicy(ApiScopes.ExecutePolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(ApiScopes.ClaimType, ApiScopes.Execute));

        return services;
    }
}
