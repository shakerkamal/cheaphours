using CheapHours.Core.Commands;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Commands;

/// <summary>Registers command dispatch: options shared by the API and the worker, and the worker's relay.</summary>
internal static class CommandsSetup
{
    /// <summary>Registers <see cref="CommandsOptions"/> (validated at start) and a shared <see cref="Random"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddCommandOptions(this IServiceCollection services)
    {
        services.AddOptions<CommandsOptions>().BindConfiguration(CommandsOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CommandsOptions>, CommandsOptionsValidator>());
        services.TryAddSingleton(Random.Shared);
        return services;
    }

    /// <summary>
    /// Registers the worker's side of command dispatch: the outbox relay, the dispatcher with its two routes and the
    /// gateway-result consumer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddCommandDispatch(this IServiceCollection services)
    {
        services.AddCommandOptions();
        services.TryAddSingleton(sp => new RetryPolicy(sp.GetRequiredService<IOptions<CommandsOptions>>().Value.ToRetryPolicyOptions(), sp.GetRequiredService<Random>()));
        services.TryAddSingleton<CommandOutcomes>();
        services.AddMetrics();
        services.TryAddSingleton<CommandMetrics>();

        services.TryAddSingleton<ProviderCommandRoute>();
        services.TryAddSingleton<GatewayCommandRoute>();
        services.AddSingleton<ICommandRoute>(sp => sp.GetRequiredService<ProviderCommandRoute>());
        services.AddSingleton<ICommandRoute>(sp => sp.GetRequiredService<GatewayCommandRoute>());

        services.TryAddSingleton<CommandDispatcher>();
        services.TryAddSingleton<IBatchHandler<CommandRequested>>(sp => sp.GetRequiredService<CommandDispatcher>());
        services.TryAddSingleton<CommandResultConsumer>();
        services.TryAddSingleton<IBatchHandler<GatewayCommandResult>>(sp => sp.GetRequiredService<CommandResultConsumer>());

        services.TryAddSingleton<OutboxRelay>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
        services.TryAddSingleton<CommandMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<CommandMonitor>());
        services.AddHostedService<CommandConsumerService<CommandRequested>>();
        services.AddHostedService<CommandConsumerService<GatewayCommandResult>>();
        return services;
    }
}
