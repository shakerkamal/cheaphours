using CheapHours.ContractTests.Broken;
using Xunit.Sdk;

namespace CheapHours.ContractTests;

/// <summary>The suite is worth something only if it fails broken providers: each targeted fact must fail, not pass or skip.</summary>
public sealed class SuiteCatchesBrokenProvidersTests
{
    [Fact]
    public async Task A_provider_that_throws_on_a_vendor_500_fails_the_failure_contract()
    {
        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => new ThrowsOnServerErrorSuite().Server_error_is_transient());

        AssertFailedNotSkipped(failure);
        Assert.Contains("threw HttpRequestException", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_throws_on_vendor_failures_fails_the_never_throw_rule()
    {
        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => new ThrowsOnServerErrorSuite().Vendor_failures_never_throw());

        AssertFailedNotSkipped(failure);
    }

    [Fact]
    public async Task A_provider_calling_an_undeclared_capability_fails_the_capability_rule()
    {
        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => new SendsUndeclaredCapabilitySuite().An_undeclared_capability_makes_no_call());

        AssertFailedNotSkipped(failure);
    }

    [Fact]
    public async Task The_broken_providers_still_pass_the_rules_they_keep()
    {
        await new ThrowsOnServerErrorSuite().Readings_are_normalised();
        await new SendsUndeclaredCapabilitySuite().A_duplicate_key_acts_once();
    }

    private static void AssertFailedNotSkipped(XunitException failure) =>
        Assert.DoesNotContain("$XunitDynamicSkip$", failure.Message, StringComparison.Ordinal);
}
