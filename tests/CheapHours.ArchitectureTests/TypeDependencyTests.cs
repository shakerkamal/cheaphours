using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace CheapHours.ArchitectureTests;

/// <summary>Boundary rules at the type level: what code in each module actually uses.</summary>
public sealed class TypeDependencyTests
{
    /// <summary>Namespaces that mean I/O, persistence, messaging, HTTP or hosting.</summary>
    private const string InfrastructureNamespaces =
        @"^(MongoDB|RabbitMQ|System\.Net|System\.IO|System\.Data|Microsoft\.AspNetCore|Microsoft\.Extensions\.Hosting|Microsoft\.EntityFrameworkCore)(\.|$)";

    public static TheoryData<string> Providers => new(RepoLayout.ProviderModules);

    [Fact]
    public void Core_types_depend_on_no_other_module()
    {
        Types().That().ResideInAssembly(RepoLayout.AssemblyOf("Core"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^CheapHours\.(?!Core(\.|$))")
            .Because("Core is the dependency-free centre of the system")
            .Check(RepoLayout.Architecture);
    }

    [Fact]
    public void Core_types_use_no_io_library()
    {
        Types().That().ResideInAssembly(RepoLayout.AssemblyOf("Core"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Because("Core holds domain types only, with no I/O")
            .Check(RepoLayout.Architecture);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Provider_types_depend_on_no_other_provider_or_infrastructure_module(string provider)
    {
        var self = provider.Replace(".", @"\.", StringComparison.Ordinal);
        var forbidden = $@"^CheapHours\.((?!{self}(\.|$))Providers\.(?!Kit(\.|$))\w+|Persistence|Messaging|Planning|Host)(\.|$)";

        Types().That().ResideInAssembly(RepoLayout.AssemblyOf(provider))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(forbidden)
            .Because("a provider may use only Core and the adapter kit")
            .Check(RepoLayout.Architecture);
    }

    [Fact]
    public void Message_contract_types_depend_only_on_core()
    {
        Types().That().ResideInAssembly(RepoLayout.AssemblyOf("Messaging.Contracts"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^CheapHours\.(?!Core(\.|$)|Messaging\.Contracts(\.|$))")
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Because("any process must be able to reference the contracts without infrastructure")
            .Check(RepoLayout.Architecture);
    }
}
