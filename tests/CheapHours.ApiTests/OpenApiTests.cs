using System.Text.Json;
using System.Text.RegularExpressions;
using CheapHours.Host.Api.Errors;

namespace CheapHours.ApiTests;

public sealed partial class OpenApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];
    private static readonly string[] CommonErrorStatuses = ["401", "403", "429", "500", "501"];

    [Fact]
    public async Task Document_lists_exactly_the_ten_v1_operations_with_their_scopes()
    {
        var doc = await GetDocumentAsync();

        var operations = Operations(doc).Select(o => $"{o.Method.ToUpperInvariant()} {o.Path} {o.Operation.GetProperty("x-required-scope").GetString()}");

        Assert.Equal(
        [
            "GET /v1/commands/{id} read",
            "GET /v1/devices read",
            "GET /v1/devices/{id} read",
            "GET /v1/devices/{id}/telemetry read",
            "GET /v1/goals/{id}/plan read",
            "GET /v1/prices read",
            "GET /v1/savings read",
            "POST /v1/devices/{id}/commands execute",
            "POST /v1/goals read",
            "POST /v1/plans/{id}/confirm execute",
        ], operations.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Api_key_scheme_is_declared_and_required_by_every_operation()
    {
        var doc = await GetDocumentAsync();

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.All(Operations(doc), o => Assert.True(o.Operation.GetProperty("security")[0].TryGetProperty("ApiKey", out _), $"{o.Path} lacks the ApiKey requirement"));
    }

    [Fact]
    public async Task Every_operation_documents_its_possible_errors_as_problem_details()
    {
        var doc = await GetDocumentAsync();

        Assert.All(Operations(doc), o =>
        {
            var responses = o.Operation.GetProperty("responses");
            foreach (var status in CommonErrorStatuses)
            {
                Assert.True(responses.TryGetProperty(status, out var response), $"{o.Method} {o.Path} lacks {status}");
                Assert.True(response.GetProperty("content").TryGetProperty("application/problem+json", out _));
                Assert.Contains("`", response.GetProperty("description").GetString(), StringComparison.Ordinal);
            }

            Assert.True(responses.GetProperty("429").GetProperty("headers").TryGetProperty("Retry-After", out _));
        });
    }

    [Fact]
    public async Task Error_catalogue_is_published_with_titles_and_descriptions()
    {
        var doc = await GetDocumentAsync();

        var published = doc.GetProperty("x-problem-types").EnumerateArray()
            .Select(p => (p.GetProperty("type").GetString(), p.GetProperty("status").GetInt32(), p.GetProperty("title").GetString(), p.GetProperty("description").GetString()));

        Assert.Equal(ErrorCatalogue.All.Select(p => ((string?)p.Type, p.Status, (string?)p.Title, (string?)p.Description)), published);
    }

    [Fact]
    public async Task Mutating_operations_accept_an_idempotency_key_and_direct_commands_return_a_location()
    {
        var doc = await GetDocumentAsync();

        Assert.All(Operations(doc).Where(o => o.Method == "post"), o =>
            Assert.Contains(o.Operation.GetProperty("parameters").EnumerateArray(), p =>
                p.GetProperty("in").GetString() == "header" && p.GetProperty("name").GetString() == "Idempotency-Key"));

        var direct = doc.GetProperty("paths").GetProperty("/v1/devices/{id}/commands").GetProperty("post").GetProperty("responses").GetProperty("202");
        Assert.True(direct.GetProperty("headers").TryGetProperty("Location", out _));
    }

    [Fact]
    public async Task Typed_ids_are_prefixed_strings_everywhere()
    {
        var doc = await GetDocumentAsync();
        var schemas = doc.GetProperty("components").GetProperty("schemas");

        foreach (var (name, prefix) in new[] { ("DeviceId", "dev_"), ("GoalId", "goal_"), ("PlanId", "plan_"), ("CommandId", "cmd_") })
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.StartsWith("^" + prefix, schema.GetProperty("pattern").GetString(), StringComparison.Ordinal);
        }

        Assert.All(Operations(doc).Where(o => o.Path.Contains("{id}", StringComparison.Ordinal)), o =>
        {
            var id = o.Operation.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "id");
            Assert.True(id.GetProperty("schema").TryGetProperty("pattern", out _), $"{o.Path} id has no pattern");
        });
    }

    [Fact]
    public async Task Schemas_follow_the_naming_convention()
    {
        var doc = await GetDocumentAsync();
        var problems = new List<string>();

        foreach (var schema in doc.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            if (schema.Value.TryGetProperty("properties", out var properties))
            {
                problems.AddRange(properties.EnumerateObject().Where(p => !CamelCase().IsMatch(p.Name)).Select(p => $"{schema.Name}.{p.Name} is not camelCase"));
            }

            if (schema.Value.TryGetProperty("enum", out var values))
            {
                problems.AddRange(values.EnumerateArray().Where(v => !PascalCase().IsMatch(v.GetString() ?? "")).Select(v => $"{schema.Name} value {v} is not PascalCase"));
            }

            if (schema.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(t => t.GetString() == "integer") && type.EnumerateArray().Any(t => t.GetString() == "string"))
            {
                problems.Add($"{schema.Name} allows numbers as strings");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Document_does_not_depend_on_the_serving_host()
    {
        var doc = await GetDocumentAsync();

        Assert.False(doc.TryGetProperty("servers", out var servers) && servers.GetArrayLength() > 0);
        Assert.Equal("CheapHours API", doc.GetProperty("info").GetProperty("title").GetString());
    }

    private async Task<JsonElement> GetDocumentAsync()
    {
        using var client = factory.CreateClient(apiKey: null);
        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IEnumerable<(string Method, string Path, JsonElement Operation)> Operations(JsonElement doc) =>
        doc.GetProperty("paths").EnumerateObject().SelectMany(path => path.Value.EnumerateObject()
            .Where(m => Methods.Contains(m.Name, StringComparer.Ordinal))
            .Select(m => (m.Name, path.Name, m.Value)));

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex CamelCase();

    [GeneratedRegex("^[A-Z][a-zA-Z0-9]*$")]
    private static partial Regex PascalCase();
}
