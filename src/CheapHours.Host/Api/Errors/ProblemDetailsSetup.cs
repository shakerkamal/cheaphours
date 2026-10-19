using System.Diagnostics;

namespace CheapHours.Host.Api.Errors;

/// <summary>Problem Details (RFC 9457) for every error the API returns.</summary>
internal static class ProblemDetailsSetup
{
    /// <summary>
    /// Registers Problem Details so every error body carries a catalogue type and a W3C trace id, and server
    /// errors never reveal exception details.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddCheapHoursProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = Customize);

    private static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        var isCatalogueType = problem.Type?.StartsWith(ErrorCatalogue.BaseUri, StringComparison.Ordinal) == true;
        if (!isCatalogueType && ErrorCatalogue.ForStatus(status) is { } entry)
        {
            problem.Type = entry.Type;
            problem.Title = entry.Title;
        }

        if (status == StatusCodes.Status500InternalServerError)
        {
            // Whatever produced the 500, the body says only that it happened.
            problem.Type = ErrorCatalogue.Internal.Type;
            problem.Title = ErrorCatalogue.Internal.Title;
            problem.Detail = null;
            problem.Extensions.Remove("exception");
        }

        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    }
}
