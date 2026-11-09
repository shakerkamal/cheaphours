using System.Text.RegularExpressions;

namespace CheapHours.ArchitectureTests;

/// <summary>
/// Where telemetry may live (ADR-009): only the Host knows the OpenTelemetry SDK; other modules use
/// <c>System.Diagnostics</c> only; Core and Planning, which have no I/O, create no spans and no metrics.
/// </summary>
public sealed partial class ObservabilityBoundaryTests
{
    private const string HostModule = "Host";

    [Fact]
    public void Only_the_host_references_an_open_telemetry_package()
    {
        var offenders = RepoLayout.SourceModules
            .Where(module => module != HostModule)
            .SelectMany(module => RepoLayout.PackageReferencesOf(module)
                .Where(package => package.StartsWith("OpenTelemetry", StringComparison.OrdinalIgnoreCase))
                .Select(package => $"{module} references {package}"))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Only CheapHours.Host may reference the OpenTelemetry SDK; other modules use System.Diagnostics:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Only_host_types_use_open_telemetry()
    {
        var offenders = Offenders(
            RepoLayout.SourceModules.Where(module => module != HostModule),
            OpenTelemetryTypes());

        Assert.True(offenders.Length == 0,
            "Only CheapHours.Host may use OpenTelemetry types:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Core_and_planning_create_no_spans_and_no_metrics()
    {
        var offenders = Offenders(["Core", "Planning"], TelemetryTypes());

        Assert.True(offenders.Length == 0,
            "Core and Planning have no I/O and record no telemetry; spans and metrics belong at the edges:\n" + string.Join('\n', offenders));
    }

    private static string[] Offenders(IEnumerable<string> modules, Regex forbidden)
    {
        var assemblies = modules.Select(module => RepoLayout.Prefix + module).ToHashSet(StringComparer.Ordinal);

        // Matched on recorded dependencies: the fluent rules only see types of loaded assemblies, and neither
        // System.Diagnostics nor OpenTelemetry is one of them.
        return RepoLayout.Architecture.Types
            .Where(type => assemblies.Contains(type.Assembly.Name.Split(',')[0]))
            .SelectMany(type => type.Dependencies
                .Where(dependency => forbidden.IsMatch(dependency.Target.FullName))
                .Select(dependency => $"{type.FullName} uses {dependency.Target.FullName}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex(@"^OpenTelemetry(\.|$)")]
    private static partial Regex OpenTelemetryTypes();

    [GeneratedRegex(@"^(OpenTelemetry(\.|$)|System\.Diagnostics\.(Activity\w*|DiagnosticSource|DiagnosticListener)$|System\.Diagnostics\.Metrics\.)")]
    private static partial Regex TelemetryTypes();
}
