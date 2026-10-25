using CheapHours.Persistence.ChargePoints;
using CheapHours.Persistence.Connections;
using CheapHours.Persistence.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace CheapHours.Persistence;

/// <summary>Registers MongoDB and the stores.</summary>
public static class MongoSetup
{
    private static readonly Lazy<bool> Conventions = new(() =>
    {
        // camelCase names, enums as strings, unknown fields tolerated (documents carry a schemaVersion).
        var pack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new EnumRepresentationConvention(BsonType.String),
            new IgnoreExtraElementsConvention(true),
        };
        ConventionRegistry.Register("CheapHours", pack, t => t.Namespace?.StartsWith("CheapHours.Persistence", StringComparison.Ordinal) == true);
        return true;
    });

    /// <summary>Registers the serialisation conventions once per process; safe to call repeatedly.</summary>
    public static void RegisterConventions() => _ = Conventions.Value;

    /// <summary>Creates a client with the configured connection string and server-selection timeout.</summary>
    /// <param name="options">The MongoDB options.</param>
    /// <returns>A new client.</returns>
    public static IMongoClient CreateClient(MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RegisterConventions();

        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        settings.ServerSelectionTimeout = options.ServerSelectionTimeout;
        return new MongoClient(settings);
    }

    /// <summary>
    /// Registers <see cref="MongoOptions"/> (section <c>Mongo</c>, connection string <c>ConnectionStrings:Mongo</c>),
    /// the client, the database and the stores.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMongoPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .Configure(o => o.ConnectionString = configuration.GetConnectionString(MongoOptions.ConnectionStringName) ?? o.ConnectionString)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Mongo: a connection string is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Mongo: a database name is required.")
            .Validate(o => o.ServerSelectionTimeout > TimeSpan.Zero, "Mongo: ServerSelectionTimeout must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton(sp => CreateClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
        services.AddSingleton<IChargePointRegistrations, ChargePointRegistrations>();
        services.AddSingleton<IDeviceStateStore, DeviceStateStore>();
        services.AddSingleton<IConnectionRegistry, ConnectionRegistry>();
        return services;
    }
}
