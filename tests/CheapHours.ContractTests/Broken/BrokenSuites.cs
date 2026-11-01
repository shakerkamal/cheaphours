using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;

namespace CheapHours.ContractTests.Broken;

/// <summary>The suite against <see cref="ThrowsOnServerErrorProvider"/>; internal, so the runner does not discover it.</summary>
internal sealed class ThrowsOnServerErrorSuite : ProviderContractTests<ThrowsOnServerErrorProvider>
{
    protected override IProviderContractHarness CreateHarness() =>
        new BrokenCloudHarness<ThrowsOnServerErrorProvider>(s => s.AddProvider<ThrowsOnServerErrorProvider, BrokenCloudOptions>("Providers:Broken"));
}

/// <summary>The suite against <see cref="SendsUndeclaredCapabilityProvider"/>; internal, so the runner does not discover it.</summary>
internal sealed class SendsUndeclaredCapabilitySuite : ProviderContractTests<SendsUndeclaredCapabilityProvider>
{
    protected override IProviderContractHarness CreateHarness() =>
        new BrokenCloudHarness<SendsUndeclaredCapabilityProvider>(s => s.AddProvider<SendsUndeclaredCapabilityProvider, BrokenCloudOptions>("Providers:Broken"));
}
