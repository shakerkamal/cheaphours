namespace CheapHours.ArchitectureTests;

public sealed class ArchitectureLoadTests
{
    [Fact]
    public void All_source_assemblies_load_into_the_architecture()
    {
        var loaded = RepoLayout.Architecture.Assemblies
            .Select(a => a.Name.Split(',')[0])
            .Where(n => n.StartsWith(RepoLayout.Prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        Assert.Equal(RepoLayout.SourceModules.Select(m => RepoLayout.Prefix + m), loaded);
    }
}
