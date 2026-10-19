using System.Globalization;
using System.Text.Json.Nodes;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CheapHours.Host.Api.OpenApi;

/// <summary>
/// Per-operation contract: required scope, security requirement, every possible error response with its
/// catalogue types, the headers clients rely on, and typed-id patterns on path parameters.
/// </summary>
internal sealed class CheapHoursOperationTransformer : IOpenApiOperationTransformer
{
    private const string ProblemJson = "application/problem+json";
    private const string ScopePolicyPrefix = "scope:";
    private const string DirectCommandOperationId = "SendDeviceCommand";

    // Every /v1 operation can answer these, whatever it does.
    private static readonly int[] CommonStatuses =
    [
        StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests,
        StatusCodes.Status500InternalServerError, StatusCodes.Status501NotImplemented,
    ];

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var scope = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .FirstOrDefault(p => p?.StartsWith(ScopePolicyPrefix, StringComparison.Ordinal) == true)?[ScopePolicyPrefix.Length..];
        if (scope is null)
        {
            return Task.CompletedTask;
        }

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-required-scope"] = new JsonNodeExtension(JsonValue.Create(scope));
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(CheapHoursDocumentTransformer.SecuritySchemeId, context.Document)] = [] }];

        AddErrorResponses(operation);
        DescribeHeaders(operation);
        AddIdPatterns(operation, context);
        return Task.CompletedTask;
    }

    private static void AddErrorResponses(OpenApiOperation operation)
    {
        operation.Responses ??= [];
        var problemContent = operation.Responses.Values
            .Select(r => r.Content)
            .FirstOrDefault(c => c?.ContainsKey(ProblemJson) == true);

        foreach (var status in CommonStatuses)
        {
            operation.Responses.TryAdd(status.ToString(CultureInfo.InvariantCulture), new OpenApiResponse { Content = problemContent });
        }

        foreach (var (code, response) in operation.Responses)
        {
            if (int.Parse(code, CultureInfo.InvariantCulture) >= 400 && response is OpenApiResponse problem)
            {
                problem.Description = string.Join(" ", ErrorCatalogue.All
                    .Where(p => p.Status.ToString(CultureInfo.InvariantCulture) == code)
                    .Select(p => $"`{p.Slug}`: {p.Title}"));
            }
        }
    }

    private static void DescribeHeaders(OpenApiOperation operation)
    {
        if (operation.Responses!.TryGetValue("429", out var tooMany) && tooMany is OpenApiResponse rateLimited)
        {
            rateLimited.Headers ??= new Dictionary<string, IOpenApiHeader>();
            rateLimited.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds to wait before retrying.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
            };
        }

        if (operation.OperationId == DirectCommandOperationId && operation.Responses.TryGetValue("202", out var accepted) && accepted is OpenApiResponse command)
        {
            command.Headers ??= new Dictionary<string, IOpenApiHeader>();
            command.Headers["Location"] = new OpenApiHeader
            {
                Description = "Where to follow the command: /v1/commands/{id}.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (parameter.In == ParameterLocation.Header && parameter.Name == V1Endpoints.IdempotencyKeyHeader)
            {
                parameter.Description = "Makes the request safe to repeat for 24 hours: the same key and body returns the first response; the same key with a different body gets 409 idempotency-key-reused.";
            }
        }
    }

    private static void AddIdPatterns(OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        foreach (var description in context.Description.ParameterDescriptions.Where(d => d.Source == BindingSource.Path))
        {
            if (!PrefixedIdSchema.TryGetPrefix(description.Type, out var prefix))
            {
                continue;
            }

            var parameter = operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(p => p.In == ParameterLocation.Path && p.Name == description.Name);
            if (parameter?.Schema is OpenApiSchema schema)
            {
                schema.Pattern = PrefixedIdSchema.Pattern(prefix);
            }
        }
    }
}
