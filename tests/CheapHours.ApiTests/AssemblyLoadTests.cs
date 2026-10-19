namespace CheapHours.ApiTests;

public sealed class AssemblyLoadTests
{
    [Fact]
    public void Assembly_under_test_loads()
    {
        var name = typeof(CheapHours.Host.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("CheapHours.Host", name);
    }
}
