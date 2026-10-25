using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using NJsonSchema.Validation;

namespace CheapHours.Providers.Ocpp.Framing;

/// <summary>
/// Validates payloads against the embedded official OCPP 1.6 JSON schemas (draft-04). Schemas are loaded
/// once, when the validator is created; register it as a singleton.
/// </summary>
public sealed class OcppSchemaValidator
{
    private const string ResourcePrefix = "Ocpp16.";
    private const string ResponseSuffix = "Response";

    private static readonly FrozenSet<ValidationErrorKind> TypeErrors = new[]
    {
        ValidationErrorKind.StringExpected,
        ValidationErrorKind.NumberExpected,
        ValidationErrorKind.IntegerExpected,
        ValidationErrorKind.BooleanExpected,
        ValidationErrorKind.ObjectExpected,
        ValidationErrorKind.ArrayExpected,
        ValidationErrorKind.NullExpected,
        ValidationErrorKind.NoTypeValidates,
    }.ToFrozenSet();

    private readonly FrozenDictionary<string, JsonSchema> _requests;
    private readonly FrozenDictionary<string, JsonSchema> _responses;

    /// <summary>Creates the validator and loads every embedded schema.</summary>
    public OcppSchemaValidator()
    {
        var requests = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var responses = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var assembly = typeof(OcppSchemaValidator).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            // Parsing an in-memory schema with no external references does no I/O, so blocking here is safe.
            var schema = JsonSchema.FromJsonAsync(reader.ReadToEnd()).GetAwaiter().GetResult();
            var name = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);

            if (name.EndsWith(ResponseSuffix, StringComparison.Ordinal))
            {
                responses[name[..^ResponseSuffix.Length]] = schema;
            }
            else
            {
                requests[name] = schema;
            }
        }

        _requests = requests.ToFrozenDictionary(StringComparer.Ordinal);
        _responses = responses.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Returns whether a request schema is embedded for <paramref name="action"/>.</summary>
    /// <param name="action">The action name.</param>
    /// <returns><see langword="true"/> when the action's request can be validated.</returns>
    public bool HasRequestSchema(string action) => _requests.ContainsKey(action);

    /// <summary>Validates a Call payload.</summary>
    /// <param name="action">The action; must have a request schema.</param>
    /// <param name="payload">The payload object.</param>
    /// <returns><see langword="null"/> when valid; otherwise the CallError code that describes the violation.</returns>
    /// <exception cref="ArgumentException">No request schema is embedded for <paramref name="action"/>.</exception>
    public OcppErrorCode? ValidateRequest(string action, JsonElement payload) =>
        Validate(Find(_requests, action), payload.GetRawText());

    /// <summary>Validates a CallResult payload we are about to send (used by tests and debug checks).</summary>
    /// <param name="action">The action being answered; must have a response schema.</param>
    /// <param name="payload">The payload object.</param>
    /// <returns><see langword="null"/> when valid; otherwise the code of the violation.</returns>
    /// <exception cref="ArgumentException">No response schema is embedded for <paramref name="action"/>.</exception>
    public OcppErrorCode? ValidateResponse(string action, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return Validate(Find(_responses, action), payload.ToJsonString());
    }

    private static JsonSchema Find(FrozenDictionary<string, JsonSchema> schemas, string action) =>
        schemas.TryGetValue(action, out var schema)
            ? schema
            : throw new ArgumentException($"No OCPP 1.6 schema is embedded for '{action}'.", nameof(action));

    private static OcppErrorCode? Validate(JsonSchema schema, string json)
    {
        var kinds = Flatten(schema.Validate(json)).Select(e => e.Kind).ToList();
        if (kinds.Count == 0)
        {
            return null;
        }

        // A missing field is the most specific problem, then a wrong type; anything else is a constraint.
        if (kinds.Contains(ValidationErrorKind.PropertyRequired))
        {
            return OcppErrorCode.OccurenceConstraintViolation;
        }

        return kinds.Any(TypeErrors.Contains)
            ? OcppErrorCode.TypeConstraintViolation
            : OcppErrorCode.PropertyConstraintViolation;
    }

    private static IEnumerable<ValidationError> Flatten(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors)
        {
            yield return error;
            if (error is ChildSchemaValidationError child)
            {
                foreach (var nested in Flatten(child.Errors.Values.SelectMany(e => e)))
                {
                    yield return nested;
                }
            }
        }
    }
}
