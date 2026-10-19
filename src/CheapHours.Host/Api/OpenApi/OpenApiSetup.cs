namespace CheapHours.Host.Api.OpenApi;

/// <summary>Registers the <c>v1</c> OpenAPI document, the committed public contract.</summary>
internal static class OpenApiSetup
{
    /// <summary>The document name, served at <c>/openapi/v1.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>Adds the <c>v1</c> document with the CheapHours transformers.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddCheapHoursOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer<CheapHoursDocumentTransformer>();
            options.AddOperationTransformer<CheapHoursOperationTransformer>();
            options.AddSchemaTransformer<CheapHoursSchemaTransformer>();
        });
}
