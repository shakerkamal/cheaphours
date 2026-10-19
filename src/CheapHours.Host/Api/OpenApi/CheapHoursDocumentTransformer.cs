using System.Text.Json.Nodes;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CheapHours.Host.Api.OpenApi;

/// <summary>
/// Document-level contract: title and version, the API-key security scheme, and the error catalogue
/// (<c>x-problem-types</c>). Servers are left out so the committed snapshot does not depend on the host.
/// </summary>
internal sealed class CheapHoursDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>The security scheme id that operations reference.</summary>
    public const string SecuritySchemeId = "ApiKey";

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "CheapHours API",
            Version = "v1",
            Description = "Devices, day-ahead prices, charging goals and plans, and device commands. "
                + "Authenticate with the X-Api-Key header. Errors are RFC 9457 Problem Details; the stable problem types are listed in x-problem-types.",
        };
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecuritySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "An API key with the read scope, or read and execute. Keys are stored hashed and never logged.",
        };

        var problemTypes = new JsonArray([.. ErrorCatalogue.All.Select(p => (JsonNode)new JsonObject
        {
            ["type"] = p.Type,
            ["status"] = p.Status,
            ["title"] = p.Title,
            ["description"] = p.Description,
        })]);
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-problem-types"] = new JsonNodeExtension(problemTypes);

        return Task.CompletedTask;
    }
}
