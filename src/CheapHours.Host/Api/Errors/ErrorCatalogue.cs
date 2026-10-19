namespace CheapHours.Host.Api.Errors;

/// <summary>
/// The public error catalogue. This is the only place problem type URIs are written; the list is part of
/// the <c>/v1</c> contract and is published in the OpenAPI document.
/// </summary>
internal static class ErrorCatalogue
{
    /// <summary>The prefix shared by every problem type URI.</summary>
    public const string BaseUri = "https://cheaphours.dev/errors/";

    public static readonly ProblemType Validation = new(
        "validation", StatusCodes.Status400BadRequest, "The request is malformed.",
        "The body, a header or a query parameter is missing, has the wrong type or is out of range. The 'errors' member lists each field.");

    public static readonly ProblemType Unauthorized = new(
        "unauthorized", StatusCodes.Status401Unauthorized, "A valid API key is required.",
        "The X-Api-Key header is missing or the key is unknown or revoked.");

    public static readonly ProblemType Forbidden = new(
        "forbidden", StatusCodes.Status403Forbidden, "The API key lacks the required scope.",
        "The key is valid but does not carry the scope this endpoint needs (read or execute).");

    public static readonly ProblemType NotFound = new(
        "not-found", StatusCodes.Status404NotFound, "The resource does not exist.",
        "No resource with this id is visible to the key's owner, or the path is not part of the API.");

    public static readonly ProblemType Conflict = new(
        "conflict", StatusCodes.Status409Conflict, "The request conflicts with the resource's current state.",
        "For example, the plan is already confirmed or has been superseded by a newer version.");

    public static readonly ProblemType IdempotencyKeyReused = new(
        "idempotency-key-reused", StatusCodes.Status409Conflict, "The Idempotency-Key was already used with a different body.",
        "Repeat a request with the same key only with the same body, or use a new key for a new request.");

    public static readonly ProblemType PlanExpired = new(
        "plan-expired", StatusCodes.Status410Gone, "The draft plan has expired.",
        "Draft plans must be confirmed before their expiry time. Request a new plan for the goal.");

    public static readonly ProblemType Unprocessable = new(
        "unprocessable", StatusCodes.Status422UnprocessableEntity, "The request is well formed but cannot be carried out.",
        "For example, the departure time is in the past or the target is below the vehicle's current charge.");

    public static readonly ProblemType CapabilityNotSupported = new(
        "capability-not-supported", StatusCodes.Status422UnprocessableEntity, "The device does not support this command.",
        "The device's capabilities do not include what the command needs, for example a power limit.");

    public static readonly ProblemType RateLimited = new(
        "rate-limited", StatusCodes.Status429TooManyRequests, "Too many requests.",
        "The key exceeded its request rate. Wait for the number of seconds in the Retry-After header.");

    public static readonly ProblemType Internal = new(
        "internal", StatusCodes.Status500InternalServerError, "An unexpected error occurred.",
        "The server failed to handle the request. Quote the traceId when reporting it; no internal details are returned.");

    public static readonly ProblemType NotImplemented = new(
        "not-implemented", StatusCodes.Status501NotImplemented, "This endpoint is not implemented yet.",
        "The endpoint is part of the v1 contract but its behaviour is still being built.");

    /// <summary>Gets every problem type, in catalogue order.</summary>
    public static IReadOnlyList<ProblemType> All { get; } =
    [
        Validation, Unauthorized, Forbidden, NotFound, Conflict, IdempotencyKeyReused, PlanExpired,
        Unprocessable, CapabilityNotSupported, RateLimited, Internal, NotImplemented,
    ];

    /// <summary>Returns the default problem type for a status code that has one.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <returns>The default entry, or <see langword="null"/> for a status outside the catalogue.</returns>
    public static ProblemType? ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => Validation,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status410Gone => PlanExpired,
        StatusCodes.Status422UnprocessableEntity => Unprocessable,
        StatusCodes.Status429TooManyRequests => RateLimited,
        StatusCodes.Status500InternalServerError => Internal,
        StatusCodes.Status501NotImplemented => NotImplemented,
        _ => null,
    };

    /// <summary>Creates a 400 <see cref="Validation"/> response listing the invalid fields.</summary>
    /// <param name="errors">Messages per field name (camelCase, as the client sent it).</param>
    /// <returns>The result to return from an endpoint.</returns>
    public static IResult ValidationFailed(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, title: Validation.Title, type: Validation.Type);
}
