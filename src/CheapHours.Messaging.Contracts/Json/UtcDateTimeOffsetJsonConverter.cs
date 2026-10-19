using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheapHours.Messaging.Contracts.Json;

/// <summary>
/// Writes timestamps as ISO 8601 UTC with a <c>Z</c> suffix and refuses to read any other offset, so a local
/// time can never slip in from outside.
/// </summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    /// <inheritdoc/>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTimeOffset();
        return value.Offset == TimeSpan.Zero
            ? value
            : throw new JsonException($"Timestamps must be UTC (with a 'Z' suffix), but '{value:O}' has offset {value.Offset}.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
