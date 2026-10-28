using CheapHours.Host.Api.Auth;

namespace CheapHours.UnitTests.Host;

public sealed class ApiKeyOptionsValidatorTests
{
    private static readonly string Hash = new('b', 64);

    private static ApiKeyEntry Entry(string name = "demo", string? hash = null, string owner = "own_demo", params string[] scopes)
    {
        var entry = new ApiKeyEntry { Name = name, Hash = hash ?? Hash, Owner = owner };
        foreach (var scope in scopes.Length == 0 ? ["read"] : scopes)
        {
            entry.Scopes.Add(scope);
        }

        return entry;
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(params ApiKeyEntry[] entries)
    {
        var options = new ApiKeyOptions();
        foreach (var entry in entries)
        {
            options.Keys.Add(entry);
        }

        return new ApiKeyOptionsValidator().Validate(null, options);
    }

    [Fact]
    public void Valid_keys_pass()
    {
        Assert.True(Validate(Entry(scopes: ["read", "execute"]), Entry("other", new string('c', 64))).Succeeded);
    }

    [Fact]
    public void No_keys_pass()
    {
        Assert.True(Validate().Succeeded);
    }

    [Fact]
    public void Unknown_scope_fails_naming_the_key_and_the_scope_but_not_the_hash()
    {
        var result = Validate(Entry("demo", scopes: ["read", "admin"]));

        Assert.True(result.Failed);
        Assert.Contains("demo", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("admin", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Hash, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-hash", "own_demo", "read", "hash")]
    [InlineData(null, "demo", "read", "owner")]
    [InlineData(null, "own_demo", "execute", "read")]
    public void Key_breaking_a_rule_fails(string? hash, string owner, string scope, string rule)
    {
        var result = Validate(Entry("broken", hash, owner, scope));

        Assert.True(result.Failed);
        Assert.Contains("broken", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(rule, result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_keys_with_one_hash_fail_naming_both()
    {
        var result = Validate(Entry("first"), Entry("second"));

        Assert.True(result.Failed);
        Assert.Contains("first", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("second", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Hash, result.FailureMessage, StringComparison.Ordinal);
    }
}
