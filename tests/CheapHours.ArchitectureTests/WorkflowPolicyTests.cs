namespace CheapHours.ArchitectureTests;

/// <summary>
/// The GitHub Actions workflows follow <see cref="WorkflowPolicy"/>; each rule is proven by planting one violation
/// into an otherwise compliant workflow.
/// </summary>
public sealed class WorkflowPolicyTests
{
    // Line endings follow the checkout (CRLF on Windows); the plants below use \n.
    private static readonly string Compliant = """
        name: planted
        on:
          pull_request:
            branches: [main]
        permissions:
          contents: read
        jobs:
          test:
            runs-on: ubuntu-24.04
            timeout-minutes: 10
            steps:
              - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567 # v7.0.1
              - uses: ./local-action
              - run: echo ok
          publish:
            needs: [test]
            runs-on: ubuntu-24.04
            timeout-minutes: 10
            permissions:
              contents: read
              packages: write
              id-token: write
              attestations: write
            steps:
              - run: echo publish
        """.ReplaceLineEndings("\n");

    private static readonly string WorkflowDirectory = Path.Combine(RepoLayout.RepoRoot, ".github", "workflows");

    [Fact]
    public void The_repository_has_workflows()
    {
        Assert.True(WorkflowFiles().Length > 0, $"No workflow found in {WorkflowDirectory}.");
    }

    [Fact]
    public void Every_workflow_follows_the_policy()
    {
        var violations = WorkflowFiles()
            .SelectMany(path => WorkflowPolicy.Check(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

        Assert.True(violations.Length == 0, "Workflow policy violations:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void A_compliant_workflow_passes()
    {
        Assert.Empty(WorkflowPolicy.Check("planted.yml", Compliant));
    }

    [Theory]
    [InlineData( // An action referenced by a mutable tag.
        "actions/checkout@0123456789abcdef0123456789abcdef01234567 # v7.0.1",
        "actions/checkout@v7",
        "planted.yml:12: 'actions/checkout@v7' must be pinned")]
    [InlineData( // A SHA without the version it stands for.
        "actions/checkout@0123456789abcdef0123456789abcdef01234567 # v7.0.1",
        "actions/checkout@0123456789abcdef0123456789abcdef01234567",
        "'actions/checkout@0123456789abcdef0123456789abcdef01234567' must be pinned")]
    [InlineData( // A write token by default.
        "permissions:\n  contents: read\njobs:",
        "permissions:\n  contents: write\njobs:",
        "planted.yml:5: the workflow-level permissions must be exactly 'contents: read'")]
    [InlineData( // No default permissions at all (the repository's default may be read-write).
        "permissions:\n  contents: read\njobs:",
        "jobs:",
        "no workflow-level permissions")]
    [InlineData( // A publish-only scope in another job.
        "    timeout-minutes: 10\n    steps:\n      - uses: actions/checkout",
        "    timeout-minutes: 10\n    permissions:\n      packages: write\n    steps:\n      - uses: actions/checkout",
        "'packages: write' is granted at job 'test'")]
    [InlineData( // A publish-only scope for every job.
        "  contents: read\njobs:",
        "  contents: read\n  id-token: write\njobs:",
        "'id-token: write' is granted at the workflow level")]
    [InlineData( // A job without a time limit.
        "    timeout-minutes: 10\n    steps:\n      - uses: actions/checkout",
        "    steps:\n      - uses: actions/checkout",
        "job 'test' has no timeout-minutes")]
    [InlineData( // A step's time limit does not bound the job.
        "    timeout-minutes: 10\n    steps:\n      - uses: actions/checkout",
        "    steps:\n      - timeout-minutes: 5\n        uses: actions/checkout",
        "job 'test' has no timeout-minutes")]
    [InlineData( // Pull-request code with a write token.
        "  pull_request:\n",
        "  pull_request_target:\n",
        "pull_request_target runs pull-request code")]
    public void A_planted_violation_is_reported(string find, string replace, string expected)
    {
        Assert.Contains(find, Compliant, StringComparison.Ordinal);

        var violations = WorkflowPolicy.Check("planted.yml", Compliant.Replace(find, replace, StringComparison.Ordinal));

        Assert.True(
            violations.Any(v => v.Contains(expected, StringComparison.Ordinal)),
            $"Expected a violation containing \"{expected}\"; got:\n" + string.Join('\n', violations));
    }

    private static string[] WorkflowFiles() =>
        Directory.Exists(WorkflowDirectory)
            ? Directory.GetFiles(WorkflowDirectory, "*.yml")
                .Concat(Directory.GetFiles(WorkflowDirectory, "*.yaml"))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
}
