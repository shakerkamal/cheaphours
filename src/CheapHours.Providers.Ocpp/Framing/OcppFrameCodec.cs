using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CheapHours.Providers.Ocpp.Framing;

/// <summary>Reads and writes OCPP-J frames. Reading never throws: bad input becomes an <see cref="OcppFrameError"/>.</summary>
public static class OcppFrameCodec
{
    /// <summary>The uniqueId used in a CallError when the bad frame's own id could not be read.</summary>
    public const string UnknownUniqueId = "-1";

    /// <summary>The longest uniqueId OCPP-J allows.</summary>
    public const int MaxUniqueIdLength = 36;

    /// <summary>The longest action name accepted.</summary>
    public const int MaxActionLength = 64;

    /// <summary>The deepest JSON nesting accepted.</summary>
    public const int MaxDepth = 32;

    private const string FormationViolationDescription = "The message is not a valid OCPP-J frame.";

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth };

    /// <summary>Reads one frame from a WebSocket text message.</summary>
    /// <param name="text">The message text.</param>
    /// <returns>The frame; an <see cref="OcppFrameError"/> when the text is not a valid OCPP-J message.</returns>
    public static OcppFrame Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, ParseOptions);
        }
        catch (JsonException)
        {
            return new OcppFrameError(null, "not valid JSON");
        }

        using (document)
        {
            return Decode(document.RootElement);
        }
    }

    /// <summary>Writes <c>[3, uniqueId, payload]</c>.</summary>
    /// <param name="uniqueId">The id of the call being answered.</param>
    /// <param name="payload">The payload object.</param>
    /// <returns>The frame text.</returns>
    public static string WriteCallResult(string uniqueId, JsonObject payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(uniqueId);
        ArgumentNullException.ThrowIfNull(payload);

        return Write(writer =>
        {
            writer.WriteNumberValue(3);
            writer.WriteStringValue(uniqueId);
            payload.WriteTo(writer);
        });
    }

    /// <summary>Writes <c>[4, uniqueId, errorCode, errorDescription, {}]</c>.</summary>
    /// <param name="uniqueId">The id of the call being answered, or <see cref="UnknownUniqueId"/>.</param>
    /// <param name="code">The error code.</param>
    /// <param name="description">A fixed description; never content taken from the frame.</param>
    /// <returns>The frame text.</returns>
    public static string WriteCallError(string uniqueId, OcppErrorCode code, string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(uniqueId);
        ArgumentNullException.ThrowIfNull(description);

        return Write(writer =>
        {
            writer.WriteNumberValue(4);
            writer.WriteStringValue(uniqueId);
            writer.WriteStringValue(code.ToString());
            writer.WriteStringValue(description);
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>The CallError that answers a malformed frame.</summary>
    /// <param name="error">The decode failure.</param>
    /// <returns>The frame text, a <c>FormationViolation</c>.</returns>
    public static string WriteFormationViolation(OcppFrameError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return WriteCallError(error.UniqueId ?? UnknownUniqueId, OcppErrorCode.FormationViolation, FormationViolationDescription);
    }

    private static OcppFrame Decode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return new OcppFrameError(null, "not a JSON array");
        }

        var length = root.GetArrayLength();
        var uniqueId = length >= 2 ? ReadUniqueId(root[1]) : null;
        if (length == 0 || root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt32(out var type))
        {
            return new OcppFrameError(uniqueId, "missing or non-integer message type id");
        }

        if (uniqueId is null)
        {
            return new OcppFrameError(null, "missing or invalid uniqueId");
        }

        return type switch
        {
            2 => DecodeCall(root, length, uniqueId),
            3 => DecodeCallResult(root, length, uniqueId),
            4 => DecodeCallError(root, length, uniqueId),
            _ => new OcppFrameError(uniqueId, "unknown message type id"),
        };
    }

    private static OcppFrame DecodeCall(JsonElement root, int length, string uniqueId)
    {
        if (length != 4)
        {
            return new OcppFrameError(uniqueId, "a Call has four elements");
        }

        var action = root[2];
        if (action.ValueKind != JsonValueKind.String || action.GetString() is not { Length: > 0 and <= MaxActionLength } name)
        {
            return new OcppFrameError(uniqueId, "missing or invalid action");
        }

        return root[3].ValueKind == JsonValueKind.Object
            ? new OcppCall(uniqueId, name, root[3].Clone())
            : new OcppFrameError(uniqueId, "the payload is not an object");
    }

    private static OcppFrame DecodeCallResult(JsonElement root, int length, string uniqueId) =>
        length == 3 && root[2].ValueKind == JsonValueKind.Object
            ? new OcppCallResult(uniqueId, root[2].Clone())
            : new OcppFrameError(uniqueId, "a CallResult has three elements and an object payload");

    private static OcppFrame DecodeCallError(JsonElement root, int length, string uniqueId)
    {
        if (length != 5
            || root[2].ValueKind != JsonValueKind.String
            || root[3].ValueKind != JsonValueKind.String
            || root[4].ValueKind != JsonValueKind.Object)
        {
            return new OcppFrameError(uniqueId, "a CallError has five elements: code, description and a details object");
        }

        return new OcppCallError(uniqueId, root[2].GetString()!, root[3].GetString()!, root[4].Clone());
    }

    private static string? ReadUniqueId(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 and <= MaxUniqueIdLength } id
            ? id
            : null;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            body(writer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
