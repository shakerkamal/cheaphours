using System.Text.RegularExpressions;

namespace CheapHours.UnitTests;

// docs/reliability.md maps every reliability-table row to tests by Class.Method; each must exist in the source.
public sealed partial class ReliabilityCatalogueTests
{
    private const int Rows = 13;

    [Fact]
    public void Catalogue_covers_every_row_with_at_least_one_test()
    {
        var rows = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "reliability.md"))
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Failure", StringComparison.Ordinal) && !l.StartsWith("| ---", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(Rows, rows.Length);
        Assert.All(rows, row => Assert.True(TestReference().IsMatch(row), $"no test named in: {row}"));
    }

    [Fact]
    public void Every_named_test_exists()
    {
        var root = RepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToLookup(Path.GetFileNameWithoutExtension, StringComparer.Ordinal);
        var catalogue = File.ReadAllText(Path.Combine(root, "docs", "reliability.md"));

        var references = TestReference().Matches(catalogue).Select(m => (Class: m.Groups["class"].Value, Method: m.Groups["method"].Value)).ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, r =>
        {
            var files = sources[r.Class].ToArray();
            Assert.True(files.Length > 0, $"no file {r.Class}.cs");
            Assert.True(
                files.Any(f => Regex.IsMatch(File.ReadAllText(f), $@"\b(void|Task)\s+{Regex.Escape(r.Method)}\(", RegexOptions.None, TimeSpan.FromSeconds(1))),
                $"{r.Class} has no test method {r.Method}");
        });
    }

    [GeneratedRegex(@"`(?<class>\w+Tests)\.(?<method>\w+)`")]
    private static partial Regex TestReference();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CheapHours.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("CheapHours.slnx not found above the test output.");
    }
}
