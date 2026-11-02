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
    public async Task A_verifier_that_accepts_anything_fails_the_webhook_verification_rules()
    {
        var suite = new AcceptsEverythingVerifierSuite();
        foreach (var fact in new Func<Task>[]
        {
            suite.A_webhook_with_a_tampered_body_is_refused,
            suite.A_webhook_signed_with_a_wrong_secret_is_refused,
            suite.A_webhook_with_a_stale_timestamp_is_refused,
            suite.A_webhook_with_a_missing_or_garbled_signature_is_refused,
        })
        {
            var failure = await Assert.ThrowsAnyAsync<XunitException>(fact);
            AssertFailedNotSkipped(failure);
            Assert.Contains("AcceptsEverythingVerifierProvider accepted", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_broken_providers_still_pass_the_rules_they_keep()
    {
        await new ThrowsOnServerErrorSuite().Readings_are_normalised();
        await new SendsUndeclaredCapabilitySuite().A_duplicate_key_acts_once();
        await new AcceptsEverythingVerifierSuite().A_signed_webhook_verifies();
        await new AcceptsEverythingVerifierSuite().Translation_is_pure_and_total();
    }

    private static void AssertFailedNotSkipped(XunitException failure) =>
        Assert.DoesNotContain("$XunitDynamicSkip$", failure.Message, StringComparison.Ordinal);
}
