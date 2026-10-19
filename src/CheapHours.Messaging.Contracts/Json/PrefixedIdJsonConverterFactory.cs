using System.Text.Json;
using System.Text.Json.Serialization;
using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts.Json;

/// <summary>Serialises every <see cref="IPrefixedId{TSelf}"/> type as its plain string form, validating on read.</summary>
public sealed class PrefixedIdJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsValueType
            && typeToConvert.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IPrefixedId<>)
                && i.GetGenericArguments()[0] == typeToConvert);
    }

    /// <inheritdoc/>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(PrefixedIdJsonConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class PrefixedIdJsonConverter<TId> : JsonConverter<TId>
        where TId : struct, IPrefixedId<TId>
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"A {typeof(TId).Name} must be a JSON string.");
            }

            var value = reader.GetString();
            return TId.TryParse(value, out var id)
                ? id
                : throw new JsonException($"'{value}' is not a valid {typeof(TId).Name}; expected the prefix '{TId.Prefix}'.");
        }

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
