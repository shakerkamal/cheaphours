using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CheapHours.ApiTests;

/// <summary>
/// The committed <c>docs/api/openapi.v1.json</c> is the public contract. A change to it must be deliberate:
/// regenerate with <c>UPDATE_SNAPSHOT=1 dotnet test --project tests/CheapHours.ApiTests</c> and review the diff.
/// </summary>
public sealed class SnapshotTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string UpdateVariable = "UPDATE_SNAPSHOT";
    private const int ContextLines = 4;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable diffs; the file is never served
    };

    [Fact]
    public async Task Generated_document_matches_the_committed_snapshot()
    {
        using var client = factory.CreateClient(apiKey: null);
        var generated = Normalise(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken));
        var path = Path.Combine(FindRepoRoot(), "docs", "api", "openapi.v1.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), TestContext.Current.CancellationToken);
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Create it with {UpdateVariable}=1 dotnet test --project tests/CheapHours.ApiTests.");
        var committed = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");

        if (committed != generated)
        {
            Assert.Fail(Describe(committed, generated));
        }
    }

    private static string Normalise(string json) => Sort(JsonNode.Parse(json))!.ToJsonString(WriteOptions) + "\n";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sort)]),
        _ => node?.DeepClone(),
    };

    private static string Describe(string committed, string generated)
    {
        var expected = committed.Split('\n');
        var actual = generated.Split('\n');
        var line = 0;
        while (line < expected.Length && line < actual.Length && expected[line] == actual[line])
        {
            line++;
        }

        var report = new StringBuilder()
            .AppendLine("The generated OpenAPI document differs from docs/api/openapi.v1.json.")
            .AppendLine($"If the change is intended, regenerate with {UpdateVariable}=1 dotnet test --project tests/CheapHours.ApiTests and review the diff.")
            .AppendLine($"First difference at line {line + 1}:");
        for (var i = Math.Max(0, line - ContextLines); i < Math.Min(Math.Max(expected.Length, actual.Length), line + ContextLines + 1); i++)
        {
            var e = i < expected.Length ? expected[i] : "<end>";
            var a = i < actual.Length ? actual[i] : "<end>";
            report.AppendLine(e == a ? $"  {i + 1,5}   {e}" : $"- {i + 1,5}   {e}\n+ {i + 1,5}   {a}");
        }

        return report.ToString();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CheapHours.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("CheapHours.slnx not found above the test output directory.");
    }
}
