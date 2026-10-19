namespace CheapHours.ArchitectureTests;

/// <summary>
/// Boundary rules at the project-file level. These catch a new reference even before any code uses it
/// (an unused project reference leaves no trace in the compiled assembly).
/// </summary>
public sealed class ModuleReferenceTests
{
    public static TheoryData<string> Providers => new(RepoLayout.ProviderModules);

    [Fact]
    public void Core_references_no_other_module_and_no_package()
    {
        Assert.Empty(RepoLayout.ProjectReferencesOf("Core"));
        Assert.Empty(RepoLayout.PackageReferencesOf("Core"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Provider_references_only_core_and_the_kit(string provider)
    {
        var disallowed = RepoLayout.ProjectReferencesOf(provider)
            .Except(["Core", "Providers.Kit"], StringComparer.Ordinal);

        Assert.True(!disallowed.Any(), $"{provider} must reference only Core and Providers.Kit, but references: {string.Join(", ", disallowed)}");
    }

    [Fact]
    public void Message_contracts_reference_only_core()
    {
        Assert.Equal(["Core"], RepoLayout.ProjectReferencesOf("Messaging.Contracts"));
        Assert.Empty(RepoLayout.PackageReferencesOf("Messaging.Contracts"));
    }

    [Fact]
    public void Every_provider_project_is_listed_and_has_a_vendor_word()
    {
        var onDisk = RepoLayout.SourceModules
            .Where(m => m.StartsWith("Providers.", StringComparison.Ordinal))
            .Where(m => m is not "Providers.Kit" and not "Providers.Kit.Testing")
            .Order(StringComparer.Ordinal);

        Assert.Equal(RepoLayout.ProviderModules.Order(StringComparer.Ordinal), onDisk);
        Assert.All(RepoLayout.ProviderModules, p =>
            Assert.Contains(RepoLayout.VendorWords, w => p.Contains(w, StringComparison.Ordinal)));
    }
}
