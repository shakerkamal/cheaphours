using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CheapHours.Messaging;

/// <summary>Registers RabbitMQ messaging: options, the connection, the topology and the publisher.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers messaging for a role. The connection string comes from <c>ConnectionStrings:RabbitMq</c>
    /// (user secrets or the environment); it is required and never logged.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="clientName">The name the broker shows for this process's connection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration, string clientName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        services.AddOptions<MessagingOptions>().Bind(configuration.GetSection(MessagingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();

        services.AddSingleton(sp =>
        {
            var connectionString = configuration.GetConnectionString(MessagingOptions.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{MessagingOptions.ConnectionStringName} is required (set it in user secrets or the environment).");
            }

            return new RabbitMqConnection(connectionString, clientName, sp.GetRequiredService<ILogger<RabbitMqConnection>>());
        });
        services.AddSingleton(sp => new MessagingTopology(sp.GetRequiredService<IOptions<MessagingOptions>>().Value));
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMqPublisher>());
        return services;
    }
}
