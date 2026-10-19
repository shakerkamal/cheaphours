using CheapHours.Host;

namespace CheapHours.UnitTests.Host;

public sealed class RoleParserTests
{
    [Theory]
    [InlineData(new[] { "--role", "api" }, nameof(HostRole.Api))]
    [InlineData(new[] { "--role", "gateway" }, nameof(HostRole.Gateway))]
    [InlineData(new[] { "--role", "worker" }, nameof(HostRole.Worker))]
    [InlineData(new[] { "--urls", "http://+:8080", "--role=api" }, nameof(HostRole.Api))]
    public void TryParse_valid_role_returns_it(string[] args, string expected)
    {
        var ok = RoleParser.TryParse(args, out var role, out var error);

        Assert.True(ok);
        Assert.Equal(expected, role.ToString());
        Assert.Null(error);
    }

    [Theory]
    [InlineData(new[] { "--role", "scheduler" }, "unknown role 'scheduler'")]
    [InlineData(new[] { "--role" }, "no role was given")]
    [InlineData(new string[0], "no role was given")]
    [InlineData(new[] { "--role", "API" }, "unknown role 'API'")]
    public void TryParse_unknown_or_missing_role_lists_valid_roles(string[] args, string reason)
    {
        var ok = RoleParser.TryParse(args, out _, out var error);

        Assert.False(ok);
        Assert.Contains(reason, error, StringComparison.Ordinal);
        Assert.Contains("api, gateway, worker", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_role_exit_code_is_two()
    {
        Assert.Equal(2, RoleParser.InvalidRoleExitCode);
    }
}
