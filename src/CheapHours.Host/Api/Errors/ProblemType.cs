namespace CheapHours.Host.Api.Errors;

/// <summary>One entry of the public error catalogue: a stable problem type URI with its status code.</summary>
/// <param name="Slug">The last path segment of the type URI, for example <c>not-found</c>.</param>
/// <param name="Status">The HTTP status code this problem is always returned with.</param>
/// <param name="Title">A short, stable, human-readable summary.</param>
/// <param name="Description">When clients get this problem and what they should do.</param>
internal sealed record ProblemType(string Slug, int Status, string Title, string Description)
{
    /// <summary>Gets the stable type URI, for example <c>https://cheaphours.dev/errors/not-found</c>.</summary>
    public string Type => ErrorCatalogue.BaseUri + Slug;

    /// <summary>Creates the Problem Details response for this type.</summary>
    /// <param name="detail">An occurrence-specific explanation; never internal details.</param>
    /// <returns>The result to return from an endpoint.</returns>
    public IResult ToResult(string? detail = null) =>
        TypedResults.Problem(detail: detail, statusCode: Status, title: Title, type: Type);
}
