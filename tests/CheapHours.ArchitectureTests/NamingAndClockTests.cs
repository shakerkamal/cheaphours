using System.Text.RegularExpressions;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace CheapHours.ArchitectureTests;

public sealed partial class NamingAndClockTests
{
    public static TheoryData<string> VendorWords => new(RepoLayout.VendorWords);

    [Theory]
    [MemberData(nameof(VendorWords))]
    public void Vendor_neutral_modules_never_name_a_vendor(string vendorWord)
    {
        Types().That().ResideInAssembly(
                RepoLayout.AssemblyOf("Core"),
                RepoLayout.AssemblyOf("Planning"),
                RepoLayout.AssemblyOf("Messaging"),
                RepoLayout.AssemblyOf("Persistence"))
            .Should().NotHaveFullNameContaining(vendorWord)
            .Because("the core never mentions a vendor by name")
            .Check(RepoLayout.Architecture);
    }

    [Fact]
    public void Domain_code_never_reads_the_system_clock()
    {
        // Matched on recorded call dependencies: the fluent NotCallAny(MethodMembers()...) only sees
        // members of loaded assemblies, and System.DateTime is not one of them.
        string[] modules = [RepoLayout.Prefix + "Core", RepoLayout.Prefix + "Planning"];

        var offenders = RepoLayout.Architecture.Types
            .Where(t => modules.Contains(t.Assembly.Name.Split(',')[0], StringComparer.Ordinal))
            .SelectMany(t => t.Dependencies.OfType<MethodCallDependency>()
                .Where(d => ClockGetters().IsMatch(d.TargetMember.FullName))
                .Select(d => $"{t.FullName} calls {d.TargetMember.FullName}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Time must come from an injected TimeProvider. Offending calls:\n" + string.Join('\n', offenders));
    }

    [GeneratedRegex(@"^System\.\w+ System\.(DateTime|DateTimeOffset)::get_(Now|UtcNow|Today)\(\)$")]
    private static partial Regex ClockGetters();
}
