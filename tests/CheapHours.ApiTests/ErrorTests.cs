using System.Net;
using System.Text.Json;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CheapHours.ApiTests;

public sealed class ErrorTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Catalogue_is_complete_with_fixed_status_codes()
    {
        var catalogue = ErrorCatalogue.All.Select(p => (p.Slug, p.Status));

        Assert.Equal(
        [
            ("validation", 400), ("unauthorized", 401), ("forbidden", 403), ("not-found", 404), ("conflict", 409),
            ("idempotency-key-reused", 409), ("plan-expired", 410), ("unprocessable", 422), ("capability-not-supported", 422),
            ("rate-limited", 429), ("internal", 500), ("not-implemented", 501),
        ], catalogue);
        Assert.Equal(ErrorCatalogue.All.Count, ErrorCatalogue.All.Select(p => p.Type).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ErrorCatalogue.All, p =>
        {
            Assert.StartsWith("https://cheaphours.dev/errors/", p.Type, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });
    }

    [Fact]
    public async Task Validation_problem_lists_the_invalid_fields()
    {
        var context = new DefaultHttpContext { RequestServices = factory.Services };
        context.Response.Body = new MemoryStream();

        await ErrorCatalogue.ValidationFailed(new Dictionary<string, string[]> { ["limit"] = ["Must be between 1 and 200."] }).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(ErrorCatalogue.Validation.Type, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Must be between 1 and 200.", doc.RootElement.GetProperty("errors").GetProperty("limit")[0].GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Internal_errors_reveal_nothing()
    {
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IApiKeyStore, ThrowingKeyStore>())));
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadKey);

        using var response = await client.GetAsync(new Uri("/v1/devices", UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "internal");
        var body = problem.GetRawText();
        Assert.DoesNotContain(ThrowingKeyStore.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.False(problem.TryGetProperty("detail", out _));
    }

    private sealed class ThrowingKeyStore : IApiKeyStore
    {
        public const string Secret = "mongodb://admin:hunter2@db";

        public ValueTask<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Could not reach {Secret}");
    }
}
