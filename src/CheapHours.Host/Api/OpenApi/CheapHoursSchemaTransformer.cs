using System.Text.Json.Nodes;
using CheapHours.Core.Devices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CheapHours.Host.Api.OpenApi;

/// <summary>
/// Schema fixes for what the generator cannot infer: typed ids (custom converter) become prefixed strings,
/// the <see cref="Capabilities"/> flags list their names, timestamps (custom converter) become typed date-time
/// strings, and Problem Details show the extensions we add.
/// </summary>
internal sealed class CheapHoursSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        var type = context.JsonTypeInfo.Type;
        if (PrefixedIdSchema.TryGetPrefix(type, out var prefix))
        {
            schema.Type = JsonSchemaType.String;
            schema.Pattern = PrefixedIdSchema.Pattern(prefix);
            schema.Description = $"An opaque id starting with '{prefix}'.";
            schema.Properties?.Clear();
        }
        else if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
        {
            // The UTC converter hides the type from the generator; a nullable timestamp may also be null.
            schema.Type = type == typeof(DateTimeOffset?) ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
            schema.Format = "date-time";
        }
        else if (type == typeof(Capabilities))
        {
            schema.Type = JsonSchemaType.String;
            schema.Enum = [.. Enum.GetNames<Capabilities>().Select(name => (JsonNode)JsonValue.Create(name))];
        }
        else if (type == typeof(ProblemDetails) || type == typeof(HttpValidationProblemDetails))
        {
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The W3C trace id of the request; quote it when reporting a problem." };
            schema.Properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Validation problems only: messages per invalid field.",
                AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } },
            };
        }

        return Task.CompletedTask;
    }
}
