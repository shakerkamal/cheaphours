using System.Net;
using System.Text.Json;

namespace CheapHours.ApiTests;

/// <summary>Assertions on Problem Details responses.</summary>
internal static class ProblemAssert
{
    /// <summary>Asserts the response is Problem Details with the given status and catalogue slug, and returns its body.</summary>
    public static async Task<JsonElement> IsProblemAsync(HttpResponseMessage response, HttpStatusCode status, string slug)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal("https://cheaphours.dev/errors/" + slug, root.GetProperty("type").GetString());
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("traceId").GetString()));
        return root;
    }
}
