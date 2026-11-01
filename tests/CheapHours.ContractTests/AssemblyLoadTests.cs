namespace CheapHours.ContractTests;

public sealed class AssemblyLoadTests
{
    [Fact]
    public void Contract_suite_assembly_loads()
    {
        var name = typeof(Providers.Kit.Testing.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("CheapHours.Providers.Kit.Testing", name);
    }
}
