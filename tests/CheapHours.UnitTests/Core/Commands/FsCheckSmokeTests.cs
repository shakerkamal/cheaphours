using FsCheck.Xunit;

namespace CheapHours.UnitTests.Core.Commands;

// Proves FsCheck.Xunit.v3 runs under xUnit v3 on Microsoft.Testing.Platform before the real properties rely on it.
public sealed class FsCheckSmokeTests
{
    [Property]
    public bool Reversing_a_string_twice_gives_it_back(string? value)
    {
        var text = value ?? string.Empty;
        return new string([.. text.Reverse().Reverse()]) == text;
    }
}
