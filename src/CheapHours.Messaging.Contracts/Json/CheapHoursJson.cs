using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CheapHours.Core.Commands;

namespace CheapHours.Messaging.Contracts.Json;

/// <summary>
/// The one JSON configuration for messages and the public API: camelCase fields, PascalCase enum strings,
/// prefixed ids as plain strings, UTC timestamps with a <c>Z</c> suffix, and unknown fields ignored.
/// </summary>
public static class CheapHoursJson
{
    /// <summary>Gets the shared, read-only serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Applies the CheapHours conventions to <paramref name="options"/> (for example ASP.NET Core's own options).</summary>
    /// <param name="options">The options to configure; must not be read-only yet.</param>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
        options.AllowOutOfOrderMetadataProperties = true;
        options.NumberHandling = JsonNumberHandling.Strict; // numbers are JSON numbers, never strings
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new PrefixedIdJsonConverterFactory());
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());

        var resolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        options.TypeInfoResolver = resolver.WithAddedModifier(AddSendResultPolymorphism);
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        options.MakeReadOnly();
        return options;
    }

    // SendResult lives in Core, which stays free of serializer attributes; its subtypes are declared here.
    private static void AddSendResultPolymorphism(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(SendResult))
        {
            return;
        }

        typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "kind",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            DerivedTypes =
            {
                new JsonDerivedType(typeof(SendResult.Accepted), nameof(SendResult.Accepted)),
                new JsonDerivedType(typeof(SendResult.Rejected), nameof(SendResult.Rejected)),
                new JsonDerivedType(typeof(SendResult.Transient), nameof(SendResult.Transient)),
            },
        };
    }
}
