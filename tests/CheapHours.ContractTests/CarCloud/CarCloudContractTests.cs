using CheapHours.Providers.CarCloud;
using CheapHours.Providers.Kit.Testing;

namespace CheapHours.ContractTests.CarCloud;

/// <summary>The car-cloud provider on the shared suite, against its WireMock vendor.</summary>
public sealed class CarCloudContractTests : ProviderContractTests<CarCloudProvider>
{
    protected override IProviderContractHarness CreateHarness() => new CarCloudHarness();
}
