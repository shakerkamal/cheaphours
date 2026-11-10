using System.Text.RegularExpressions;

namespace CheapHours.ArchitectureTests;

/// <summary>
/// The rules every GitHub Actions workflow follows, checked on the workflow text without a YAML package: actions
/// from outside the repository pinned to a commit SHA with their version in a comment, a read-only default token,
/// write scopes for packages, OIDC and attestations only in the publish job, a time limit on every job, and no
/// <c>pull_request_target</c> trigger.
/// </summary>
internal static partial class WorkflowPolicy
{
    public const string PublishJob = "publish";

    /// <summary>Checks one workflow's text and returns one message per violation, naming the file and line.</summary>
    public static IReadOnlyList<string> Check(string workflow, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var violations = new List<string>();
        var jobOfLine = JobOfEachLine(lines, out var jobs);

        CheckPinnedActions(workflow, lines, violations);
        CheckWorkflowPermissions(workflow, lines, violations);
        CheckWriteScopes(workflow, lines, jobOfLine, violations);
        CheckTimeouts(workflow, lines, jobs, violations);
        CheckNoPullRequestTarget(workflow, lines, violations);
        return violations;
    }

    private static void CheckPinnedActions(string workflow, string[] lines, List<string> violations)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = UsesLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var reference = match.Groups["ref"].Value.Trim('"', '\'');
            if (reference.StartsWith("./", StringComparison.Ordinal) || reference.StartsWith("docker://", StringComparison.Ordinal))
            {
                continue;
            }

            if (!PinnedReference().IsMatch(reference) || !VersionComment().IsMatch(match.Groups["comment"].Value))
            {
                violations.Add($"{workflow}:{i + 1}: '{reference}' must be pinned to a full commit SHA with its version in a comment (owner/action@<40 hex> # vX.Y.Z).");
            }
        }
    }

    private static void CheckWorkflowPermissions(string workflow, string[] lines, List<string> violations)
    {
        var start = Array.FindIndex(lines, line => Indent(line) == 0 && Code(line).StartsWith("permissions:", StringComparison.Ordinal));
        if (start < 0)
        {
            violations.Add($"{workflow}: no workflow-level permissions; the default token must be 'permissions: contents: read'.");
            return;
        }

        var inline = Code(lines[start])["permissions:".Length..].Trim();
        var entries = lines
            .Skip(start + 1)
            .TakeWhile(line => IsBlank(line) || Indent(line) > 0)
            .Where(line => !IsBlank(line))
            .Select(line => WhiteSpace().Replace(Code(line), " "))
            .ToArray();

        if (inline.Length > 0 || entries is not ["contents: read"])
        {
            violations.Add($"{workflow}:{start + 1}: the workflow-level permissions must be exactly 'contents: read'; grant more per job.");
        }
    }

    private static void CheckWriteScopes(string workflow, string[] lines, string?[] jobOfLine, List<string> violations)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = PublishOnlyWriteScope().Match(Code(lines[i]));
            if (!match.Success || jobOfLine[i] == PublishJob)
            {
                continue;
            }

            var where = jobOfLine[i] is { } job ? $"job '{job}'" : "the workflow level";
            violations.Add($"{workflow}:{i + 1}: '{match.Groups["scope"].Value}: write' is granted at {where}; only the '{PublishJob}' job may have it.");
        }
    }

    private static void CheckTimeouts(string workflow, string[] lines, IReadOnlyList<Job> jobs, List<string> violations)
    {
        foreach (var job in jobs)
        {
            // Only the job's own keys count: a step's timeout-minutes is indented deeper.
            var body = Enumerable.Range(job.KeyIndex + 1, job.LastIndex - job.KeyIndex)
                .Where(i => !IsBlank(lines[i]))
                .ToArray();
            var childIndent = body.Length > 0 ? Indent(lines[body[0]]) : -1;
            var hasTimeout = body.Any(i => Indent(lines[i]) == childIndent && Code(lines[i]).StartsWith("timeout-minutes:", StringComparison.Ordinal));

            if (!hasTimeout)
            {
                violations.Add($"{workflow}:{job.KeyIndex + 1}: job '{job.Name}' has no timeout-minutes.");
            }
        }
    }

    private static void CheckNoPullRequestTarget(string workflow, string[] lines, List<string> violations)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (Code(lines[i]).Contains("pull_request_target", StringComparison.Ordinal))
            {
                violations.Add($"{workflow}:{i + 1}: pull_request_target runs pull-request code with a write token; use pull_request.");
            }
        }
    }

    /// <summary>The job each line belongs to (null outside <c>jobs:</c>), and each job's lines.</summary>
    private static string?[] JobOfEachLine(string[] lines, out IReadOnlyList<Job> jobs)
    {
        var owners = new string?[lines.Length];
        var found = new List<Job>();
        jobs = found;

        var start = Array.FindIndex(lines, line => Indent(line) == 0 && Code(line) == "jobs:");
        if (start < 0)
        {
            return owners;
        }

        var jobIndent = -1;
        string? current = null;
        var keyIndex = 0;
        var lastContent = start;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (IsBlank(lines[i]))
            {
                continue;
            }

            var indent = Indent(lines[i]);
            if (indent == 0)
            {
                break;
            }

            if (jobIndent < 0)
            {
                jobIndent = indent;
            }

            if (indent == jobIndent)
            {
                if (current is not null)
                {
                    found.Add(new Job(current, keyIndex, lastContent));
                }

                current = Code(lines[i]).TrimEnd(':');
                keyIndex = i;
            }

            owners[i] = current;
            lastContent = i;
        }

        if (current is not null)
        {
            found.Add(new Job(current, keyIndex, lastContent));
        }

        return owners;
    }

    /// <summary>The line without its comment and surrounding white space.</summary>
    private static string Code(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i].Trim();
            }
        }

        return line.Trim();
    }

    private static bool IsBlank(string line) => Code(line).Length == 0;

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>A job: its name, the index of its key line and of its last line.</summary>
    private sealed record Job(string Name, int KeyIndex, int LastIndex);

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s*(?<ref>[^\s#]+)\s*(?<comment>#.*)?$")]
    private static partial Regex UsesLine();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedReference();

    [GeneratedRegex(@"^#\s*v\d+(\.\d+)*")]
    private static partial Regex VersionComment();

    [GeneratedRegex(@"^(?<scope>packages|id-token|attestations):\s*write$")]
    private static partial Regex PublishOnlyWriteScope();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
