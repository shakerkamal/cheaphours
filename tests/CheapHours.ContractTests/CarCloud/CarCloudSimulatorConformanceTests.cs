using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit.Testing;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>
/// The CarCloud facts that matter for the vendor's behaviour, run against the real simulator: if the WireMock
/// fake and the simulator disagree on normalisation, idempotency, token refresh or revocation, this fails.
/// </summary>
public sealed class CarCloudSimulatorConformanceTests
{
    [Fact]
    public Task Readings_are_normalised() => new Suite().Readings_are_normalised();

    [Fact]
    public Task A_duplicate_key_acts_once() => new Suite().A_duplicate_key_acts_once();

    [Fact]
    public Task Unauthorized_refreshes_once_and_retries_once() => new Suite().Unauthorized_refreshes_once_and_retries_once();

    [Fact]
    public Task A_revoked_refresh_token_fails_clearly() => new Suite().A_revoked_refresh_token_fails_clearly();

    /// <summary>The suite on the simulator; private, so the runner does not discover its other facts.</summary>
    private sealed class Suite : ProviderContractTests<CarCloudProvider>
    {
        protected override IProviderContractHarness CreateHarness() => new CarCloudSimulatorHarness();
    }
}
