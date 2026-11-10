using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheapHours.ApiTests;

/// <summary>
/// The dashboard calls only documented operations (web-dashboard "Every called path is in the contract"). Every
/// call in <c>dashboard/site/js</c> is written <c>request('&lt;METHOD&gt;', '&lt;path&gt;'</c>; this test finds them all
/// and checks each method and path against the committed <c>docs/api/openapi.v1.json</c>.
/// </summary>
public sealed partial class DashboardContractTests
{
    [Fact]
    public void Every_call_the_dashboard_makes_is_in_the_committed_contract()
    {
        var root = FindRepoRoot();
        var operations = DocumentedOperations(File.ReadAllText(Path.Combine(root, "docs", "api", "openapi.v1.json")));
        var sources = Directory.GetFiles(Path.Combine(root, "dashboard", "site", "js"), "*.mjs")
            .Where(f => Path.GetFileName(f) != "api.mjs")
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText, StringComparer.Ordinal);

        var report = Check(sources, operations);

        Assert.Empty(report.Problems);
        Assert.True(report.Calls.Count >= 8, $"Expected the dashboard's calls, found {report.Calls.Count}.");
    }

    [Fact]
    public void A_call_to_a_path_outside_the_contract_fails_the_check()
    {
        var operations = new HashSet<string>(StringComparer.Ordinal) { "GET /v1/devices", "GET /v1/goals/{}/plan" };
        var planted = """
            // Comments are ignored: request(anything)
            await api.request('GET', '/v1/devices', { query: { limit: 200 } });
            await api.request('GET', `/v1/goals/${encodeURIComponent(goal.id)}/plan`);
            await api.request('GET', '/v1/nothing');
            await api.request('POST', '/v1/devices');
            await api.request(method, path);
            """;

        var report = Check(new Dictionary<string, string>(StringComparer.Ordinal) { ["planted.mjs"] = planted }, operations);

        Assert.Equal(
            [
                "planted.mjs: GET /v1/nothing is not in docs/api/openapi.v1.json",
                "planted.mjs: POST /v1/devices is not in docs/api/openapi.v1.json",
                "planted.mjs: 1 request( call(s) not written as request('<METHOD>', '<path>'",
            ],
            report.Problems);
    }

    private static (List<string> Calls, List<string> Problems) Check(Dictionary<string, string> sources, HashSet<string> operations)
    {
        var calls = new List<string>();
        var problems = new List<string>();
        foreach (var (name, text) in sources)
        {
            var source = Comment().Replace(text, string.Empty);
            var literal = CallPattern().Matches(source);
            foreach (Match match in literal)
            {
                var path = Normalise(match.Groups["path"].Value);
                var call = $"{match.Groups["method"].Value} {path}";
                calls.Add(call);
                if (!operations.Contains(call))
                {
                    problems.Add($"{name}: {call} is not in docs/api/openapi.v1.json");
                }
            }

            // A call with a computed method or path would escape the check, so none is allowed.
            var all = AnyCallPattern().Count(source);
            if (all != literal.Count)
            {
                problems.Add($"{name}: {all - literal.Count} request( call(s) not written as request('<METHOD>', '<path>'");
            }
        }

        return (calls, problems);
    }

    private static HashSet<string> DocumentedOperations(string openApi)
    {
        using var document = JsonDocument.Parse(openApi);
        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                operations.Add($"{method.Name.ToUpperInvariant()} {Normalise(path.Name)}");
            }
        }

        return operations;
    }

    // `${…}` in a template and `{name}` in the document both become `{}`; a query string is dropped.
    private static string Normalise(string path) =>
        Placeholder().Replace(path.Split('?')[0], "{}");

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

    [GeneratedRegex(@"\brequest\(\s*'(?<method>GET|POST|PUT|PATCH|DELETE)'\s*,\s*(?<quote>['`])(?<path>[^'`]*)\k<quote>")]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"\brequest\(")]
    private static partial Regex AnyCallPattern();

    [GeneratedRegex(@"\$\{[^}]*\}|\{[^}]*\}")]
    private static partial Regex Placeholder();

    // Block comments and line comments; the dashboard's code has no `//` inside strings.
    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
