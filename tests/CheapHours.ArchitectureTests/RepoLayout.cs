using System.Reflection;
using System.Xml.Linq;
using ArchUnitNET.Loader;
using Architecture = ArchUnitNET.Domain.Architecture;

namespace CheapHours.ArchitectureTests;

/// <summary>The solution's modules, read from disk (project files) and from the built assemblies.</summary>
internal static class RepoLayout
{
    public const string Prefix = "CheapHours.";

    public static readonly string[] ProviderModules = ["Providers.Ocpp", "Providers.CarCloud", "Providers.HeatPumpCloud"];

    /// <summary>Words that identify a vendor; extend when a provider is added (a test checks coverage).</summary>
    public static readonly string[] VendorWords = ["Ocpp", "CarCloud", "HeatPump"];

    public static readonly Assembly[] Assemblies =
    [
        typeof(Core.AssemblyMarker).Assembly,
        typeof(Messaging.Contracts.AssemblyMarker).Assembly,
        typeof(Planning.AssemblyMarker).Assembly,
        typeof(Persistence.AssemblyMarker).Assembly,
        typeof(Messaging.AssemblyMarker).Assembly,
        typeof(Providers.Kit.AssemblyMarker).Assembly,
        typeof(Providers.Kit.Testing.AssemblyMarker).Assembly,
        typeof(Providers.Ocpp.AssemblyMarker).Assembly,
        typeof(Providers.CarCloud.AssemblyMarker).Assembly,
        typeof(Providers.HeatPumpCloud.AssemblyMarker).Assembly,
        typeof(Host.AssemblyMarker).Assembly,
    ];

    public static readonly Architecture Architecture = new ArchLoader().LoadAssemblies(Assemblies).Build();

    public static Assembly AssemblyOf(string module) =>
        Assemblies.Single(a => a.GetName().Name == Prefix + module);

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Module names (without the prefix) of every project under <c>src/</c>.</summary>
    public static IReadOnlyList<string> SourceModules { get; } =
        Directory.GetDirectories(Path.Combine(RepoRoot, "src"), Prefix + "*")
            .Select(d => Path.GetFileName(d)[Prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Modules (without the prefix) that the given module's project file references.</summary>
    public static IReadOnlyList<string> ProjectReferencesOf(string module) =>
        ItemIncludes(module, "ProjectReference")
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))[Prefix.Length..])
            .ToArray();

    /// <summary>Package ids the given module's project file references.</summary>
    public static IReadOnlyList<string> PackageReferencesOf(string module) => ItemIncludes(module, "PackageReference");

    /// <summary>Project files of every simulator under <c>simulators/</c>, relative to the repository root with forward slashes.</summary>
    public static IReadOnlyList<string> SimulatorProjects { get; } =
        Directory.Exists(Path.Combine(RepoRoot, "simulators"))
            ? Directory.GetFiles(Path.Combine(RepoRoot, "simulators"), "*.csproj", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(RepoRoot, p).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>Full paths of the projects that the given project file (relative to the repository root) references.</summary>
    public static IReadOnlyList<string> ResolvedProjectReferencesOf(string projectFile)
    {
        var path = Path.Combine(RepoRoot, projectFile);
        var directory = Path.GetDirectoryName(path)!;
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))))
            .ToArray();
    }

    /// <summary>Project paths listed in <c>CheapHours.slnx</c>, with forward slashes.</summary>
    public static IReadOnlyList<string> SolutionProjects { get; } =
        XDocument.Load(Path.Combine(RepoRoot, "CheapHours.slnx"))
            .Descendants("Project")
            .Select(e => (string?)e.Attribute("Path"))
            .OfType<string>()
            .Select(p => p.Replace('\\', '/'))
            .ToArray();

    private static string[] ItemIncludes(string module, string itemName)
    {
        var path = Path.Combine(RepoRoot, "src", Prefix + module, Prefix + module + ".csproj");
        return XDocument.Load(path)
            .Descendants(itemName)
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToArray();
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
