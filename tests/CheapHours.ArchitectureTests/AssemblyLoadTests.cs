namespace CheapHours.ArchitectureTests;

public sealed class AssemblyLoadTests
{
    [Fact]
    public void Assembly_under_test_loads()
    {
        var name = typeof(CheapHours.Core.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("CheapHours.Core", name);
    }
}
