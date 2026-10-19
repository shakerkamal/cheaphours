using System.Security.Claims;
using System.Text.Encodings.Web;
using CheapHours.Host.Api.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Api.Auth;

/// <summary>
/// Authenticates <c>X-Api-Key</c>: hashes the presented key and looks the hash up. Challenges and
/// forbidden responses are Problem Details from the catalogue. The key is never logged or echoed.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore store,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The authentication scheme name.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>The header that carries the key.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return AuthenticateResult.NoResult();
        }

        var record = await store.FindByHashAsync(ApiKeyHasher.Hash(values[0]!), Context.RequestAborted);
        if (record is null)
        {
            return AuthenticateResult.Fail("Unknown API key.");
        }

        List<Claim> claims =
        [
            new(ClaimTypes.Name, record.Name),
            new(ApiScopes.OwnerClaimType, record.OwnerId.Value),
            .. record.Scopes.Select(scope => new Claim(ApiScopes.ClaimType, scope)),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <inheritdoc/>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        return WriteProblemAsync(ErrorCatalogue.Unauthorized, $"Send a valid API key in the {HeaderName} header.");
    }

    /// <inheritdoc/>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(ErrorCatalogue.Forbidden, "The API key does not carry the scope this endpoint needs.");

    private async Task WriteProblemAsync(ProblemType problem, string detail)
    {
        Response.StatusCode = problem.Status;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Type = problem.Type, Title = problem.Title, Status = problem.Status, Detail = detail },
        });
    }
}
